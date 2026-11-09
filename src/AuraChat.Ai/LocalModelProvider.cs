using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Ai;

/// <summary>
/// 本地模型模式：支持 Ollama 等本地服务，不需要 API Key，不计费。
/// 用于隐私优先或离线场景。
/// </summary>
public sealed class LocalModelProvider : IModelProvider
{
    private readonly HttpClient _http;

    public string Name => "Local";

    public LocalModelProvider(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<JsonObject> AskAsync(Endpoint endpoint, JsonObject body, int timeoutMs, CancellationToken ct)
    {
        using var resp = await SendAsync(endpoint, body, timeoutMs, stream: false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        Endpoint endpoint,
        JsonObject body,
        int timeoutMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var resp = await SendAsync(endpoint, body, timeoutMs, stream: true, ct).ConfigureAwait(false);
        await foreach (var piece in ReadStreamAsync(resp, ct).ConfigureAwait(false))
            yield return piece;
    }

    // ---------------- HTTP 调用（本地服务通常不需要认证）----------------

    private async Task<HttpResponseMessage> SendAsync(
        Endpoint endpoint,
        JsonObject body,
        int timeoutMs,
        bool stream,
        CancellationToken ct)
    {
        body["stream"] = stream;

        var req = new HttpRequestMessage(HttpMethod.Post, endpoint.Url("chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // 本地模型通常不需要 Authorization，但如果配置了 Key 仍然发送
        if (endpoint.HasKey)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LlmException($"本地模型调用失败（{(int)resp.StatusCode}）：{err}");
            }
            return resp;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"本地模型调用超时（{timeoutMs / 1000.0:F1}秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"本地模型连接失败（请确认服务已启动）：{ex.Message}");
        }
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (JsonSerializer.Deserialize<JsonNode>(text) is not JsonObject obj)
            throw new LlmException("本地模型返回格式错误：不是 JSON 对象");
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
                throw new LlmException("本地模型调用失败：" + (err["message"]?.ToString() ?? "对方返回了错误"));

            if (obj["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var delta = (choices[0] as JsonObject)?["delta"] as JsonObject;
            var piece = delta?["content"]?.ToString();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }
}
