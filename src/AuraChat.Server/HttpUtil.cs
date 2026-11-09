using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace AuraChat.Server;

/// <summary>HTTP 小工具：响应、读体、取 token。行为对齐原 Node 版 web/server.js。</summary>
public static class HttpUtil
{
    public const long MaxBodyBytes = 12L * 1024 * 1024;

    public static Task SendAsync(HttpContext ctx, int status, string body, string type = "text/plain; charset=utf-8")
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.Headers.CacheControl = "no-store";
        return ctx.Response.WriteAsync(body, Encoding.UTF8);
    }

    public static Task JsonAsync(HttpContext ctx, JsonNode? body)
        => SendAsync(ctx, 200, body?.ToJsonString() ?? "null", "application/json; charset=utf-8");

    public static Task JsonAsync(HttpContext ctx, object body)
        => SendAsync(ctx, 200, JsonSerializer.Serialize(body), "application/json; charset=utf-8");

    /// <summary>带状态码的 JSON 响应（现有重载默认 200 不变）。</summary>
    public static Task JsonAsync(HttpContext ctx, int status, JsonNode? body)
        => SendAsync(ctx, status, body?.ToJsonString() ?? "null", "application/json; charset=utf-8");

    public static Task Fail(HttpContext ctx, string reason) => JsonAsync(ctx, new JsonObject { ["ok"] = false, ["reason"] = reason });

    /// <summary>发二进制（图片/音频/图标）。带 ETag 时支持 304。</summary>
    public static async Task SendBytesAsync(HttpContext ctx, byte[] data, string mime, string cacheControl = "no-store", string? etag = null)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = mime;
        ctx.Response.Headers.CacheControl = cacheControl;
        ctx.Response.ContentLength = data.Length;
        if (etag is not null)
        {
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                ctx.Response.StatusCode = 304;
                ctx.Response.ContentLength = null;
                return;
            }
        }
        await ctx.Response.Body.WriteAsync(data, ctx.RequestAborted);
    }

    /// <summary>读 JSON 请求体，上限 12MB，空体视为 {}。</summary>
    public static async Task<JsonObject> ReadBodyAsync(HttpContext ctx, long maxBytes = MaxBodyBytes)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidOperationException("请求体太大");
            ms.Write(buffer, 0, read);
        }
        if (ms.Length == 0) return new JsonObject();
        var text = Encoding.UTF8.GetString(ms.ToArray());
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>Bearer 优先，其次 cookie oc_s。</summary>
    public static string? TokenOf(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        var cookie = ctx.Request.Headers.Cookie.ToString();
        if (!string.IsNullOrEmpty(cookie))
        {
            foreach (var part in cookie.Split(';'))
            {
                var kv = part.Trim();
                if (kv.StartsWith("oc_s=", StringComparison.Ordinal)) return kv[5..];
            }
        }
        return null;
    }

    /// <summary>去掉 data:URL 前缀，留纯 base64。</summary>
    public static string StripDataUrl(string? s)
    {
        var v = (s ?? "").Trim();
        var i = v.IndexOf(',');
        return i >= 0 && v.StartsWith("data:", StringComparison.Ordinal) ? v[(i + 1)..] : v;
    }

    public static string? Query(HttpContext ctx, string key)
    {
        var q = ctx.Request.Query[key];
        return q.Count > 0 ? q[0] : null;
    }

    public static byte[] Base64ToBytes(string base64) => Convert.FromBase64String(base64);

    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".ico" => "image/x-icon",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" => "audio/ogg",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// host 字符串 → 监听地址。空值和解析不了的值一律退回 loopback：
    /// 配置写错时宁可缩在小圈里，也不能悄悄把服务暴露到所有网卡。
    /// </summary>
    public static IPAddress ParseHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return IPAddress.Loopback;
        return host.Trim() switch
        {
            "0.0.0.0" or "*" or "any" => IPAddress.Any,
            "::" => IPAddress.IPv6Any,
            "localhost" or "127.0.0.1" => IPAddress.Loopback,
            _ => IPAddress.TryParse(host.Trim(), out var ip) ? ip : IPAddress.Loopback,
        };
    }

    /// <summary>
    /// 旧版 URL ?token= 的兼容截止时间（UTC，含当天）。随 2.1 候选发布起算，给还拿着旧链接的客户端
    /// 一个明确的迁移窗口；过了这个点 query token 直接按无凭证处理（401），不再兜底。
    /// 兼容不是永久的：URL 里的会话凭证会落进日志/历史/Referer，这条通道必须死。
    /// </summary>
    public static readonly DateTimeOffset QueryTokenCompatUntil = new(2026, 12, 1, 23, 59, 59, TimeSpan.Zero);

    public static bool QueryTokenExpired(DateTimeOffset now) => now > QueryTokenCompatUntil;

    /// <summary>
    /// 会话凭证候选：Authorization Bearer / cookie oc_s 优先（TokenOf）；
    /// 旧版 URL ?token= 只是有界兼容兜底，排最后，命中时由调用方打日志提醒迁移。
    /// 兼容窗口（QueryTokenCompatUntil）过后返回 (null, true)：仍能区分「用了旧通道」打日志，但一律 401。
    /// </summary>
    public static (string? Token, bool FromQuery) SessionTokenOf(HttpContext ctx, DateTimeOffset? now = null)
    {
        var t = TokenOf(ctx);
        if (!string.IsNullOrEmpty(t)) return (t, false);
        var q = Query(ctx, "token");
        if (string.IsNullOrEmpty(q)) return (null, false);
        return QueryTokenExpired(now ?? DateTimeOffset.UtcNow) ? (null, true) : (q, true);
    }
}
