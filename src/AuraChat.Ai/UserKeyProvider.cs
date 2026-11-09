using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Ai;

/// <summary>
/// 用户自带Key模式：直接调用上游模型服务，不计费，不限制。
/// 用户自己承担上游费用和配额。这是当前默认行为。
/// </summary>
public sealed class UserKeyProvider : IModelProvider
{
    private readonly HttpClient _http;

    public string Name => "UserKey";

    public UserKeyProvider(HttpClient http)
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

    // ---------------- HTTP 调用 ----------------

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
                throw new LlmException($"模型调用失败（{(int)resp.StatusCode}）：{err}");
            }
            return resp;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"模型调用超时（{timeoutMs / 1000.0:F1}秒）");
        }
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (JsonSerializer.Deserialize<JsonNode>(text) is not JsonObject obj)
            throw new LlmException("模型返回格式错误：不是 JSON 对象");
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
                throw new LlmException("模型调用失败：" + (err["message"]?.ToString() ?? "对方返回了错误"));

            if (obj["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var delta = (choices[0] as JsonObject)?["delta"] as JsonObject;
            var piece = delta?["content"]?.ToString();
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }
}
