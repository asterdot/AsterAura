using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuraChat.Core;

namespace AuraChat.App;

/// <summary>
/// 客户端到服务的通道。嵌入式模式下 BaseUrl 是 127.0.0.1，自己填了域名就是那个域名——
/// 两种模式走同一套 HTTP 代码，网页端、桌面端、手机端行为一致。
/// </summary>
public sealed class AppApi : IDisposable
{
    private readonly HttpClient _http;
    private string _token = "";

    public string BaseUrl { get; private set; } = "";
    public JsonObject? Me { get; private set; }
    public string UserName { get; private set; } = "";
    public bool LoggedIn => _token.Length > 0;

    public AppApi()
    {
        var handler = new HttpClientHandler
        {
            // 默认严格校验服务器证书；自签证书/隧道链不全的场景用 AURACHAT_ALLOW_INSECURE_TLS=1 显式放开（TlsPolicy）
            ServerCertificateCustomValidationCallback = (_, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || TlsPolicy.AllowInsecure,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
    }

    /// <summary>把用户输入的地址规整成 http(s)://host:port（只填 ip 就补 :2332）。</summary>
    public static string Normalize(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        if (!text.Contains("://", StringComparison.Ordinal))
            text = (text.Contains("localhost") || text.StartsWith("127.") || text.StartsWith("192.168.") || text.StartsWith("10."))
                ? "http://" + text
                : "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return "";
        var builder = new UriBuilder(uri);
        if (builder.Port <= 0 || (uri.Port == 80 && uri.Scheme == "https") || (uri.Port == 443 && uri.Scheme == "http"))
        {
            // 没写端口：本地和服务端默认 2332
        }
        if (uri.IsDefaultPort) builder.Port = 2332;
        // 去掉路径，只留站点根
        builder.Path = "/";
        builder.Query = "";
        return builder.Uri.ToString().TrimEnd('/');
    }

    public void Use(string baseUrl) => BaseUrl = baseUrl.TrimEnd('/');

    public void SetToken(string token) { _token = token ?? ""; }
    public string Token => _token;

    // ---------------------------------------------------------------- 基础请求

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, ClientUrls.Of(BaseUrl, path));
        if (_token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        if (BaseUrl.Length == 0) return Err("还没填地址");
        try
        {
            using var req = Request(method, path, body);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (text.Length == 0) return Err($"服务没回话（HTTP {(int)resp.StatusCode}）");
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return Err("服务回的不是 JSON");
            if (!resp.IsSuccessStatusCode && node["reason"] is null && node["message"] is null)
                node["reason"] = $"HTTP {(int)resp.StatusCode}";
            return node;
        }
        catch (TaskCanceledException) { return Err("等太久了，服务没响应"); }
        catch (HttpRequestException ex) { return Err("连不上：" + ex.Message); }
        catch (Exception ex) { return Err("出错了：" + ex.Message); }
    }

    public Task<JsonObject> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonObject> PostAsync(string path, JsonNode? body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body, ct);
    public Task<JsonObject> PatchAsync(string path, JsonNode? body, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, path, body, ct);
    public Task<JsonObject> DeleteAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, path, null, ct);

    public static JsonObject Err(string reason) => new() { ["ok"] = false, ["reason"] = reason };
    public static bool Ok(JsonObject res) => res["ok"] is null ? res["reason"] is null : res["ok"]!.GetValue<bool>();

    // ---------------------------------------------------------------- 会话

    public async Task<JsonObject> StateAsync(CancellationToken ct = default) => await GetAsync("api/state", ct);

    public async Task<JsonObject> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var res = await PostAsync("api/login", new JsonObject { ["username"] = username, ["password"] = password }, ct);
        if (res["token"]?.ToString() is { Length: > 0 } token)
        {
            SetToken(token);
            UserName = username;
        }
        return res;
    }

    public async Task<JsonObject> SetupAsync(JsonObject payload, CancellationToken ct = default)
    {
        var res = await PostAsync("api/setup", payload, ct);
        if (!Ok(res)) return res;
        // setup 不直接发 token，接着登录一次
        var username = payload["username"]?.ToString() ?? "";
        var password = payload["password"]?.ToString() ?? "";
        return await LoginAsync(username, password, ct);
    }

    public Task<JsonObject> LogoutAsync(CancellationToken ct = default) => PostAsync("api/logout", null, ct);

    public Task<JsonObject> ConfigAsync(CancellationToken ct = default) => GetAsync("api/config", ct);

    public Task<JsonObject> PatchConfigAsync(JsonObject patch, CancellationToken ct = default) => PatchAsync("api/config", patch, ct);

    public Task<JsonObject> ModelsAsync(CancellationToken ct = default) => GetAsync("api/models", ct);

    public Task<JsonObject> PersonasAsync(CancellationToken ct = default) => GetAsync("api/personas", ct);

    // ---------------------------------------------------------------- 聊天

    public Task<JsonObject> MessagesAsync(string chatId, CancellationToken ct = default) => GetAsync($"api/messages?chatId={Uri.EscapeDataString(chatId)}", ct);

    public Task<JsonObject> MessagesFileAsync(string chatId, string file, long? before = null, CancellationToken ct = default)
    {
        var url = $"api/messages?chatId={Uri.EscapeDataString(chatId)}&file={file}";
        if (before is > 0) url += "&before=" + before;
        return GetAsync(url, ct);
    }

    public Task<JsonObject> SendAsync(string chatId, string text, CancellationToken ct = default) =>
        PostAsync("api/messages", new JsonObject { ["chatId"] = chatId, ["text"] = text }, ct);

    public Task<JsonObject> SendVoiceAsync(string chatId, string base64, string mime, double dur, CancellationToken ct = default) =>
        PostAsync("api/messages", new JsonObject
        {
            ["chatId"] = chatId,
            ["voice"] = new JsonObject { ["data"] = base64, ["mime"] = mime, ["dur"] = dur },
        }, ct);

    public Task<JsonObject> SendStickerAsync(string chatId, string md5, CancellationToken ct = default) =>
        PostAsync("api/messages", new JsonObject { ["chatId"] = chatId, ["sticker"] = md5 }, ct);

    public Task<JsonObject> TypingAsync(bool typing, CancellationToken ct = default) =>
        PostAsync("api/typing", new JsonObject { ["typing"] = typing }, ct);

    // ---------------------------------------------------------------- 媒体

    // 媒体/头像/背景/SSE 的 URL 一律不带 token（会话凭证不进 URL、日志和 Referer），
    // 凭证统一走 Authorization Bearer：见 ClientUrls 与 AuthedRequest。

    private HttpRequestMessage AuthedRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        if (_token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    public string MediaUrl(string mediaId, string chatId = "") => ClientUrls.Media(BaseUrl, mediaId);

    public string StickerUrl(string md5) => ClientUrls.Sticker(BaseUrl, md5);

    public string AvatarUrl() => ClientUrls.Avatar(BaseUrl, DateTime.Now.Ticks);

    public string BackgroundUrl() => ClientUrls.Background(BaseUrl, DateTime.Now.Ticks);

    public async Task<byte[]?> MediaBytesAsync(string url, CancellationToken ct = default)
    {
        // Bearer 只发给可信 BaseUrl 下的媒体端点；别的 URL 一律不发请求也不发凭证（返回 null 走过期占位）
        if (!ClientUrls.IsTrustedMediaUrl(BaseUrl, url)) return null;
        try
        {
            using var req = AuthedRequest(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- SSE

    /// <summary>
    /// 订阅实时事件。只在第一次连上时返回；断线自己重连，事件通过回调抛出来。
    /// SSE 凭证走 Authorization 头，URL 不带 token。
    /// </summary>
    public async Task ListenAsync(Func<string, JsonObject, Task> onEvent, CancellationToken ct, Action<string>? onStatus = null)
    {
        var wait = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = AuthedRequest(HttpMethod.Get, ClientUrls.Stream(BaseUrl));
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    onStatus?.Invoke($"连不上实时通道（HTTP {(int)resp.StatusCode}）");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    wait = Math.Min(wait * 2, 15);
                    continue;
                }

                wait = 1;
                onStatus?.Invoke("已连接");
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var json = line[5..].Trim();
                    if (json.Length == 0) continue;
                    try
                    {
                        if (JsonNode.Parse(json) is not JsonObject evt) continue;
                        var type = evt["type"]?.ToString() ?? "";
                        if (type.Length > 0) await onEvent(type, evt);
                    }
                    catch { /* 单条坏帧跳过 */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                onStatus?.Invoke("实时通道断了，正在重连…（" + ex.Message + "）");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (OperationCanceledException) { return; }
            wait = Math.Min(wait * 2, 15);
        }
    }

    public void Dispose() => _http.Dispose();
}
