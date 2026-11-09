using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Ai;

/// <summary>
/// 官方额度中转模式：调用 Aster Aura 中转服务，检查余额，扣费记录。
/// 用户购买额度，由服务端统一管理上游 API Key 和计费。
/// </summary>
public sealed class AuraQuotaProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly string _quotaServiceUrl;
    private readonly Func<string?> _getAccessToken; // 获取当前用户访问令牌

    public string Name => "AuraQuota";

    /// <summary>
    /// 构造官方额度提供者。
    /// </summary>
    /// <param name="http">HTTP 客户端</param>
    /// <param name="quotaServiceUrl">额度服务基础地址（例如：https://aura.example.com/api/quota）</param>
    /// <param name="getAccessToken">获取当前用户访问令牌的函数</param>
    public AuraQuotaProvider(HttpClient http, string quotaServiceUrl, Func<string?> getAccessToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _quotaServiceUrl = quotaServiceUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(quotaServiceUrl));
        _getAccessToken = getAccessToken ?? throw new ArgumentNullException(nameof(getAccessToken));
    }

    public async Task<JsonObject> AskAsync(Endpoint endpoint, JsonObject body, int timeoutMs, CancellationToken ct)
    {
        // 请求前检查余额（可选，也可以让服务端统一处理）
        // await CheckQuotaAsync(ct).ConfigureAwait(false);

        using var resp = await SendAsync(endpoint, body, timeoutMs, stream: false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        Endpoint endpoint,
        JsonObject body,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 请求前检查余额（可选）
        // await CheckQuotaAsync(ct).ConfigureAwait(false);

        using var resp = await SendAsync(endpoint, body, timeoutMs, stream: true, ct).ConfigureAwait(false);
        await foreach (var piece in ReadStreamAsync(resp, ct).ConfigureAwait(false))
            yield return piece;
    }

    // ---------------- 中转服务调用 ----------------

    private async Task<HttpResponseMessage> SendAsync(
        Endpoint endpoint,
        JsonObject body,
        int timeoutMs,
        bool stream,
        CancellationToken ct)
    {
        body["stream"] = stream;

        // 构造中转请求：原始请求体 + 元信息
        var quotaRequest = new JsonObject
        {
            ["provider"] = endpoint.ProviderId,
            ["model"] = endpoint.ModelOr(body["model"]?.ToString()),
            ["body"] = body,
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_quotaServiceUrl}/model/invoke")
        {
            Content = new StringContent(quotaRequest.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // 携带用户访问令牌
        var token = _getAccessToken();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                
                // 特殊处理额度不足
                if ((int)resp.StatusCode == 402) // Payment Required
                    throw new LlmException("额度不足，请充值后继续使用");
                
                throw new LlmException($"中转服务调用失败（{(int)resp.StatusCode}）：{err}");
            }
            
            return resp;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"中转服务调用超时（{timeoutMs / 1000.0:F1}秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"中转服务连接失败：{ex.Message}");
        }
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (JsonSerializer.Deserialize<JsonNode>(text) is not JsonObject obj)
            throw new LlmException("中转服务返回格式错误：不是 JSON 对象");
        return obj;
    }

    private static async IAsyncEnumerable<string> ReadStreamAsync(
        HttpResponseMessage resp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
            if (line.Contains("[DONE]")) break;

            var json = line["data: ".Length..];
            JsonNode? node;
            try { node = JsonSerializer.Deserialize<JsonNode>(json); }
            catch (JsonException) { continue; }

            if (node is not JsonObject obj) continue;

            if (obj["error"] is JsonObject err)
                throw new LlmException("中转服务返回错误：" + (err["message"]?.ToString() ?? "对方返回了错误"));

            if (obj["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var delta = (choices[0] as JsonObject)?["delta"] as JsonObject;
            var piece = delta?["content"]?.ToString();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }

    // 可选：请求前检查余额
    // private async Task CheckQuotaAsync(CancellationToken ct)
    // {
    //     var token = _getAccessToken();
    //     if (string.IsNullOrWhiteSpace(token)) return;
    //
    //     var req = new HttpRequestMessage(HttpMethod.Get, $"{_quotaServiceUrl}/balance");
    //     req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    //
    //     var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
    //     if (!resp.IsSuccessStatusCode) return; // 余额检查失败不阻塞请求
    //
    //     var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    //     var balance = JsonSerializer.Deserialize<JsonObject>(json)?["balance"]?.GetValue<double>();
    //     if (balance <= 0)
    //         throw new LlmException("额度不足，请充值后继续使用");
    // }
}
