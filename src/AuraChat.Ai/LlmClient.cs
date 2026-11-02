using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;

namespace AuraChat.Ai;

/// <summary>模型调用失败（面向用户的中文文案，调用方直接拿 Message 显示）。</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// OpenAI 兼容协议的模型客户端：POST {base}/chat/completions。
/// 原版 core/llm.js 只是把调用转给 DSH 宿主；独立版自己发 HTTP，
/// 所以超时、重试、错误文案都在这里落地。
/// </summary>
public sealed class LlmClient : ILlmClient
{
    /// <summary>429 / 5xx 重试一次的退避时间。</summary>
    private const int RetryDelayMs = 1500;

    /// <summary>模型服务要求的 top_logprobs 上限（OpenAI 规范是 0~20，guard 默认要 25，这里夹一下）。</summary>
    private const int MaxTopLogprobs = 20;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        // 中文提示词别被转成 \uXXXX，否则请求体积翻几倍
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FeatureHost _host;
    private readonly HttpClient _http;
    private readonly AreaLog _log;
    private readonly string? _baseUrlOverride;
    private readonly string? _apiKeyOverride;

    public LlmClient(FeatureHost host, HttpClient http)
        : this(host, http, null, null)
    {
    }

    /// <summary>固定接口地址 / 密钥的客户端（guard 用自己那套 baseUrl + apiKey 时构造）。</summary>
    public LlmClient(FeatureHost host, HttpClient http, string? baseUrl, string? apiKey)
    {
        _host = host;
        _http = http;
        _baseUrlOverride = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim();
        _apiKeyOverride = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _log = host.Log.Child("ai");
    }

    // ---------------- ILlmClient ----------------

    public async Task<string> AskAsync(
        LlmOptions options,
        IReadOnlyList<LlmMessage> messages,
        CancellationToken ct = default)
    {
        var ep = Resolve(options);
        var body = BuildBody(options, messages, ep.ModelOr(options.Model), stream: false, logprobs: false);
        using var resp = await SendAsync(ep, body, options.TimeoutMs, stream: false, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        // 原版 askModel 返回 out.trim()，这里保持一致
        return ExtractContent(json["choices"] is JsonArray { Count: > 0 } choices
                ? (choices[0] as JsonObject)?["message"] as JsonObject
                : null).Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LlmOptions options,
        IReadOnlyList<LlmMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ep = Resolve(options);
        var body = BuildBody(options, messages, ep.ModelOr(options.Model), stream: true, logprobs: false);
        using var resp = await SendAsync(ep, body, options.TimeoutMs, stream: true, ct).ConfigureAwait(false);
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == ':') continue;                                   // SSE 注释/心跳
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data is "[DONE]") break;

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch (JsonException) { continue; }                             // 半行/坏行直接跳过

            if (node is not JsonObject obj) continue;

            if (obj["error"] is JsonObject err)
                throw new LlmException("模型调用失败：" + (err["message"]?.ToString() ?? "对方返回了错误"));

            if (obj["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var delta = (choices[0] as JsonObject)?["delta"] as JsonObject;
            var piece = delta?["content"]?.ToString();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }

    public async Task<LlmLogprobResult> AskWithLogprobsAsync(
        LlmOptions options,
        IReadOnlyList<LlmMessage> messages,
        CancellationToken ct = default)
    {
        var ep = Resolve(options);
        var top = Math.Clamp(options.TopLogprobs <= 0 ? 1 : options.TopLogprobs, 1, MaxTopLogprobs);
        var body = BuildBody(options, messages, ep.ModelOr(options.Model), stream: false, logprobs: true, top);
        using var resp = await SendAsync(ep, body, options.TimeoutMs, stream: false, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        return ParseLogprobs(json);
    }

    // ---------------- 请求组装 ----------------

    private Endpoint Resolve(LlmOptions options)
    {
        var ep = EndpointResolver.Resolve(_host.Config, options.Provider, options.Model, _baseUrlOverride, _apiKeyOverride);

        if (string.IsNullOrWhiteSpace(ep.BaseUrl))
            throw new LlmException($"模型调用失败：还没填接口地址（设置 → 模型，provider = {ep.ProviderId}）");

        if (ep.NeedsKey && !ep.HasKey)
            throw new LlmException("模型调用失败：还没填 API Key（设置 → 模型 → 密钥）");

        if (string.IsNullOrWhiteSpace(ep.Model))
            throw new LlmException("模型调用失败：还没选模型（设置 → 模型）");

        return ep;
    }

    private static JsonObject BuildBody(
        LlmOptions o,
        IReadOnlyList<LlmMessage> messages,
        string model,
        bool stream,
        bool logprobs,
        int topLogprobs = 0)
    {
        var list = new JsonArray();

        // system 拼在最前（原版 askModel 是把 system 单独传给宿主，协议层等价于首条 system 消息）
        if (!string.IsNullOrWhiteSpace(o.System))
            list.Add(new JsonObject { ["role"] = "system", ["content"] = o.System });

        foreach (var m in messages)
        {
            if (m is null) continue;
            list.Add(new JsonObject
            {
                ["role"] = NormalizeRole(m.Role),
                ["content"] = m.Content ?? "",
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = list,
            ["stream"] = stream,
        };

        // o1 / o3 / gpt-5 这类推理模型不接受 temperature，也不接受 max_tokens
        var reasoningOnly = IsReasoningOnlyModel(model);
        if (!reasoningOnly) body["temperature"] = Math.Clamp(o.Temperature, 0d, 2d);

        if (o.MaxTokens > 0)
            body[reasoningOnly ? "max_completion_tokens" : "max_tokens"] = o.MaxTokens;

        // reasoningEffort = off 时不要发这个字段（部分服务看到就 400）
        var effort = (o.ReasoningEffort ?? "").Trim().ToLowerInvariant();
        if (effort is "low" or "medium" or "high") body["reasoning_effort"] = effort;

        if (logprobs)
        {
            body["logprobs"] = true;
            body["top_logprobs"] = Math.Clamp(topLogprobs <= 0 ? 1 : topLogprobs, 1, MaxTopLogprobs);
        }

        return body;
    }

    private static string NormalizeRole(string? role) => (role ?? "").Trim().ToLowerInvariant() switch
    {
        "system" => "system",
        "assistant" => "assistant",
        _ => "user",
    };

    private static bool IsReasoningOnlyModel(string model)
    {
        var m = (model ?? "").Trim();
        return m.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
    }

    private HttpRequestMessage BuildRequest(Endpoint ep, string payload, bool stream)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, ep.Url("chat/completions"));
        if (ep.HasKey) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
        req.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>
    /// 发请求并处理重试：429 / 5xx 退避 1.5 秒重试一次，其余错误直接抛中文异常。
    /// 非 2xx 时把响应体里的 error.message 摘出来附在文案后面，方便排查。
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        Endpoint ep,
        JsonObject body,
        int timeoutMs,
        bool stream,
        CancellationToken ct)
    {
        var payload = body.ToJsonString(JsonWriteOptions);
        var timeout = Math.Max(1000, timeoutMs);
        _log.Debug($"请求 {ep.ProviderId}/{ep.Model} stream={stream} timeout={timeout}ms");

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            HttpResponseMessage? resp = null;
            try
            {
                using var req = BuildRequest(ep, payload, stream);
                resp = await _http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException($"模型调用失败：等了 {Math.Round(timeout / 1000d, 1)} 秒对方没回话（超时）");
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException($"模型调用失败：连不上 {HostOf(ep.BaseUrl)}（{ex.Message}）", ex);
            }

            if (resp.IsSuccessStatusCode) return resp;

            var status = (int)resp.StatusCode;
            var detail = await ReadErrorDetailAsync(resp, ct).ConfigureAwait(false);
            resp.Dispose();

            var retryable = status is 429 or >= 500;
            if (retryable && attempt == 0)
            {
                _log.Warn($"模型返回 HTTP {status}，{RetryDelayMs}ms 后重试一次（{ep.ProviderId}/{ep.Model}）");
                await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                continue;
            }

            throw new LlmException(StatusMessage(status, detail));
        }
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return "";

            try
            {
                if (JsonNode.Parse(text) is JsonObject obj)
                {
                    var message = obj["error"] is JsonObject err
                        ? err["message"]?.ToString()
                        : obj["message"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(message)) text = message!;
                }
            }
            catch (JsonException) { /* 不是 JSON 就用原文 */ }

            text = Redact.Apply(text.Replace('\r', ' ').Replace('\n', ' ').Trim());
            return text.Length > 160 ? text[..160] + "…" : text;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>面向用户的中文错误文案（对齐「模型调用失败：HTTP 401 密钥不对」这种写法）。</summary>
    private static string StatusMessage(int status, string detail) => status switch
    {
        400 => "模型调用失败：HTTP 400 请求参数不对" + DetailSuffix(detail),
        401 => "模型调用失败：HTTP 401 密钥不对或没填" + DetailSuffix(detail),
        403 => "模型调用失败：HTTP 403 这个密钥没有权限" + DetailSuffix(detail),
        404 => "模型调用失败：HTTP 404 地址不对（检查接口地址和模型名）" + DetailSuffix(detail),
        408 => "模型调用失败：HTTP 408 对方超时" + DetailSuffix(detail),
        413 => "模型调用失败：HTTP 413 内容太长了" + DetailSuffix(detail),
        429 => "模型调用失败：HTTP 429 请求太频繁，稍等再试" + DetailSuffix(detail),
        >= 500 => $"模型调用失败：HTTP {status} 对方服务端出错了" + DetailSuffix(detail),
        _ => $"模型调用失败：HTTP {status}" + DetailSuffix(detail),
    };

    private static string DetailSuffix(string detail)
        => string.IsNullOrWhiteSpace(detail) ? "" : "：" + detail;

    private static string HostOf(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return uri.Authority;
        return string.IsNullOrWhiteSpace(baseUrl) ? "模型服务" : baseUrl;
    }

    // ---------------- 响应解析 ----------------

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmException("模型调用失败：对方返回了空内容");

        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
            {
                if (obj["error"] is JsonObject err && obj["choices"] is null)
                    throw new LlmException("模型调用失败：" + (err["message"]?.ToString() ?? "对方返回了错误"));
                return obj;
            }
        }
        catch (JsonException) { /* 落到下面统一报错 */ }

        throw new LlmException("模型调用失败：看不懂对方返回的内容（" + Truncate(text, 120) + "）");
    }

    /// <summary>取 message.content，兼容部分服务把 content 返回成片段数组的写法。</summary>
    private static string ExtractContent(JsonObject? message)
    {
        if (message is null) return "";
        var content = message["content"];
        if (content is null) return "";

        if (content is JsonArray parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
                if (part is JsonObject p) sb.Append(p["text"]?.ToString() ?? "");
            return sb.ToString();
        }

        return content.ToString();
    }

    private static LlmLogprobResult ParseLogprobs(JsonObject json)
    {
        var tokens = new List<TokenLogprob>();
        var text = "";

        if (json["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice)
        {
            text = ExtractContent(choice["message"] as JsonObject);

            if (choice["logprobs"] is JsonObject logprobs && logprobs["content"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not JsonObject token) continue;

                    var tops = new Dictionary<string, double>(StringComparer.Ordinal);
                    if (token["top_logprobs"] is JsonArray alternatives)
                    {
                        foreach (var alt in alternatives)
                        {
                            if (alt is not JsonObject a) continue;
                            var altToken = a["token"]?.ToString() ?? "";
                            if (altToken.Length == 0) continue;
                            tops[altToken] = JsonUtil.Num(a["logprob"], 0);
                        }
                    }

                    tokens.Add(new TokenLogprob(
                        token["token"]?.ToString() ?? "",
                        JsonUtil.Num(token["logprob"], 0),
                        tops));
                }
            }
        }

        return new LlmLogprobResult(text, tokens);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
