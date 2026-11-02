using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuraChat.Server;

/// <summary>
/// HTTP 服务本体：Kestrel 监听 + 静态页 + /api/* + SSE。
/// 语义对齐原 Node 版 web/server.js：固定端口用「根路径模式」（/ 就是网页，/api/x 就是接口）。
/// </summary>
public sealed class ServerHost(ApiContext api, AppLog log) : IAsyncDisposable
{
    private readonly AreaLog _log = log.Child("web");
    private WebApplication? _app;
    private Router? _router;

    public bool Running => _app is not null;
    public string? BoundHost { get; private set; }
    public int BoundPort { get; private set; }

    public Router Router => _router ??= BuildRouter();

    private Router BuildRouter()
    {
        var router = new Router();
        RouteLoader.LoadAll(router, api);
        _log.Info($"已注册 {router.Size} 条接口");
        return router;
    }

    /// <summary>启动或重启监听。端口没变就不动。</summary>
    public async Task<(bool Ok, string? Reason)> StartAsync(string host, int port)
    {
        if (_app is not null && BoundHost == host && BoundPort == port) return (true, null);
        await StopAsync();

        try
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = "AuraChat",
                ContentRootPath = api.Store.Root,
            });
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = HttpUtil.MaxBodyBytes;
                options.Listen(HttpUtil.ParseHost(host), port);
            });
            builder.Logging.ClearProviders();

            var app = builder.Build();
            app.Run(HandleAsync);

            await app.StartAsync();
            _app = app;
            BoundHost = host;
            BoundPort = port;
            api.Sse.StartHeartbeat(TimeSpan.FromSeconds(25));
            _log.Info($"服务已启动 http://{host}:{port}/");
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.Warn($"监听 {host}:{port} 失败：{ex.Message}");
            return (false, ex.Message);
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { await _app.DisposeAsync(); } catch { }
        _app = null;
        BoundPort = 0;
        BoundHost = null;
    }

    /// <summary>按当前配置重绑端口（PATCH api/config 后调用）。</summary>
    public async Task RebindAsync()
    {
        var host = api.Str("web", "host");
        if (string.IsNullOrWhiteSpace(host)) host = "0.0.0.0";
        var port = (int)api.Num(2332, "web", "port");
        await StartAsync(host, port);
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        try
        {
            var pathname = ctx.Request.Path.Value ?? "/";
            const string prefix = AuraChatInfo.Prefix;

            // 固定端口（根路径模式）：/aurachat/... 是错的，提示一下
            if (pathname == prefix || pathname.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                await HttpUtil.SendAsync(ctx, 404, "not found: 固定端口只认根路径（/），不要加 /aurachat 前缀");
                return;
            }
            pathname = prefix + (pathname.StartsWith('/') ? pathname : "/" + pathname);

            if (pathname == prefix + "/api/stream")
            {
                var me = api.Auth.SessionUserOf(HttpUtil.Query(ctx, "token"));
                if (me is null)
                {
                    await HttpUtil.SendAsync(ctx, 401, "{\"ok\":false,\"reason\":\"没登录\"}", "application/json; charset=utf-8");
                    return;
                }
                await api.Sse.AttachAsync(ctx, ApiContext.ChatIdOf(me.Subject));
                return;
            }

            if (pathname.StartsWith(prefix + "/api/", StringComparison.Ordinal))
            {
                await HandleApiAsync(ctx, pathname[prefix.Length..]);
                return;
            }

            await ServeStaticAsync(ctx, pathname);
        }
        catch (Exception ex)
        {
            _log.Warn($"请求出错 {ctx.Request.Path}：{ex.Message}");
            try { await HttpUtil.JsonAsync(ctx, new JsonObject { ["ok"] = false, ["message"] = ex.Message }); } catch { }
        }
    }

    private async Task HandleApiAsync(HttpContext ctx, string pathWithSlash)
    {
        var p = pathWithSlash.TrimStart('/');
        var handler = Router.Match(ctx.Request.Method, p);
        if (handler is null)
        {
            await HttpUtil.JsonAsync(ctx, new JsonObject { ["ok"] = false, ["reason"] = "不认识这个接口：" + p });
            return;
        }
        try
        {
            await handler(new ApiCall(ctx, api, p));
        }
        catch (Exception ex)
        {
            _log.Warn($"接口 {p} 出错：{ex.Message}");
            try { await HttpUtil.JsonAsync(ctx, new JsonObject { ["ok"] = false, ["message"] = ex.Message }); } catch { }
        }
    }

    private async Task ServeStaticAsync(HttpContext ctx, string pathname)
    {
        const string prefix = AuraChatInfo.Prefix;
        var rel = pathname == prefix || pathname == prefix + "/" ? "index.html" : pathname[(prefix.Length + 1)..];
        if (rel.Contains("..") || rel.Contains('\\'))
        {
            await HttpUtil.SendAsync(ctx, 403, "no");
            return;
        }
        var file = Path.Combine(api.PublicRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            await HttpUtil.SendAsync(ctx, 404, "not found");
            return;
        }
        var bytes = await File.ReadAllBytesAsync(file, ctx.RequestAborted);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = HttpUtil.MimeOf(file);
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await api.Sse.DisposeAsync();
    }
}
