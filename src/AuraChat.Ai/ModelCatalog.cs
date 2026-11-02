using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;

namespace AuraChat.Ai;

/// <summary>
/// 模型目录：远端 GET {base}/models 拿列表，拿不到就退回 Providers 里的静态清单（绝不抛异常），
/// 外加当前默认选择和「测试连通性」探针。
/// </summary>
public sealed class ModelCatalog : IModelCatalog
{
    /// <summary>成功拿到的远端列表缓存多久（秒）；静态回退不缓存，接口恢复了立刻能用。</summary>
    private const int CacheSeconds = 60;

    /// <summary>列模型最多等多久（秒）。</summary>
    private const int ListTimeoutSeconds = 8;

    /// <summary>探针的 maxTokens。</summary>
    private const int ProbeMaxTokens = 8;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FeatureHost _host;
    private readonly HttpClient _http;
    private readonly AreaLog _log;
    private readonly Lock _gate = new();

    private JsonArray? _cache;
    private DateTime _cachedAt;
    private string _cacheKey = "";

    public ModelCatalog(FeatureHost host, HttpClient http)
    {
        _host = host;
        _http = http;
        _log = host.Log.Child("ai");
    }

    /// <summary>配置改了 provider / 地址之后调一下，下次一定重新拉。</summary>
    public void ClearCache()
    {
        lock (_gate)
        {
            _cache = null;
            _cacheKey = "";
        }
    }

    // ---------------- IModelCatalog ----------------

    public async Task<JsonArray> ListModelsAsync(CancellationToken ct = default)
    {
        var ep = EndpointResolver.Resolve(_host.Config, null, null);

        // 配了 key + 地址才值得问远端；否则直接给静态表，别白等 8 秒
        if (string.IsNullOrWhiteSpace(ep.BaseUrl) || (ep.NeedsKey && !ep.HasKey))
            return Providers.StaticCatalog();

        var key = CacheKey(ep);
        lock (_gate)
        {
            if (_cache is not null && _cacheKey == key && (DateTime.UtcNow - _cachedAt).TotalSeconds < CacheSeconds)
                return (JsonArray)_cache.DeepClone();
        }

        try
        {
            var remote = await FetchModelsAsync(ep, ct).ConfigureAwait(false);
            if (remote.Count > 0)
            {
                lock (_gate)
                {
                    _cache = (JsonArray)remote.DeepClone();
                    _cachedAt = DateTime.UtcNow;
                    _cacheKey = key;
                }
                return remote;
            }

            _log.Debug($"GET {ep.Url("models")} 返回空列表，用静态模型清单兜底");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"拉模型列表失败，用静态清单兜底：{ex.Message}");
        }

        return Providers.StaticCatalog();
    }

    public JsonObject DefaultModel()
    {
        var section = _host.Config.Current["model"] as JsonObject;
        var provider = (section?["provider"]?.ToString() ?? "").Trim();
        var model = (section?["model"]?.ToString() ?? "").Trim();
        var effort = (section?["reasoningEffort"]?.ToString() ?? "").Trim();
        var baseUrl = (section?["baseUrl"]?.ToString() ?? "").Trim();

        // 没选 provider：填了自定义地址就当自定义服务，否则用第一个预设
        var preset = Providers.Find(provider)
            ?? (provider.Length == 0 && baseUrl.Length > 0
                ? Providers.Get(Providers.CustomId)
                : Providers.All[0]);

        return new JsonObject
        {
            ["provider"] = preset.Id,
            ["model"] = model.Length > 0 ? model : preset.DefaultModel,
            ["reasoningEffort"] = effort.Length > 0 ? effort : "low",
        };
    }

    public async Task<JsonObject> TestAsync(string? provider, string? model, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var ep = EndpointResolver.Resolve(_host.Config, provider, model);

            if (string.IsNullOrWhiteSpace(ep.BaseUrl))
                return Fail("还没填接口地址（设置 → 模型 → 接口地址）");

            if (ep.NeedsKey && !ep.HasKey)
                return Fail("还没填 API Key（设置 → 模型 → 密钥）");

            var modelId = ep.ModelOr(model);
            if (string.IsNullOrWhiteSpace(modelId))
                return Fail("还没选模型（设置 → 模型）");

            var body = new JsonObject
            {
                ["model"] = modelId,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "ping" },
                },
                ["max_tokens"] = ProbeMaxTokens,
                ["stream"] = false,
            };

            var timeout = Math.Clamp(_host.Int(20_000, "model", "timeoutMs"), 5_000, 30_000);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Post, ep.Url("chat/completions"));
            if (ep.HasKey) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
            req.Headers.Accept.ParseAdd("application/json");
            req.Content = new StringContent(body.ToJsonString(JsonWriteOptions), Encoding.UTF8, "application/json");

            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = ExtractError(text);
                return Fail($"HTTP {(int)resp.StatusCode} " + (detail.Length > 0 ? detail : StatusHint((int)resp.StatusCode)));
            }

            // 200 但 body 里带 error 也算失败（少数服务这么干）
            try
            {
                if (JsonNode.Parse(text) is JsonObject obj && obj["error"] is JsonObject err)
                    return Fail(err["message"]?.ToString() ?? "对方返回了错误");
            }
            catch (JsonException) { /* 不是 JSON 就当成功，有的服务只回纯文本 */ }

            _log.Info($"模型连通性测试通过：{ep.ProviderId}/{modelId}（{watch.ElapsedMilliseconds}ms）");
            return new JsonObject
            {
                ["ok"] = true,
                ["ms"] = watch.ElapsedMilliseconds,
                ["model"] = modelId,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("等太久没回话（超时）");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ---------------- 内部 ----------------

    private async Task<JsonArray> FetchModelsAsync(Endpoint ep, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ListTimeoutSeconds));

        using var req = new HttpRequestMessage(HttpMethod.Get, ep.Url("models"));
        if (ep.HasKey) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.Debug($"GET {ep.Url("models")} → HTTP {(int)resp.StatusCode}");
            return new JsonArray();
        }

        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return new JsonArray();

        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { return new JsonArray(); }

        // 绝大多数服务是 { data: [ { id } ] }，少数直接给数组
        var items = root switch
        {
            JsonObject obj => obj["data"] as JsonArray ?? obj["models"] as JsonArray,
            JsonArray arr => arr,
            _ => null,
        };
        if (items is null) return new JsonArray();

        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var id = (item as JsonObject)?["id"]?.ToString()
                     ?? (item as JsonObject)?["name"]?.ToString()
                     ?? (item?.ToString() ?? "");
            id = id.Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;

            result.Add(new JsonObject
            {
                ["provider"] = ep.ProviderId,
                ["model"] = id,
                ["label"] = id,
            });
        }

        return result;
    }

    private static string CacheKey(Endpoint ep) => ep.ProviderId + "|" + ep.BaseUrl + "|" + (ep.HasKey ? "1" : "0");

    private static string ExtractError(string text)
    {
        var detail = "";
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
            {
                detail = obj["error"] is JsonObject err
                    ? err["message"]?.ToString() ?? ""
                    : obj["message"]?.ToString() ?? "";
            }
        }
        catch (JsonException) { /* 用原文截一段 */ }

        if (string.IsNullOrWhiteSpace(detail)) detail = text;
        detail = Redact.Apply((detail ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim());
        return detail.Length > 120 ? detail[..120] + "…" : detail;
    }

    private static string StatusHint(int status) => status switch
    {
        400 => "请求参数不对",
        401 => "密钥不对或没填",
        403 => "这个密钥没有权限",
        404 => "地址不对（检查接口地址和模型名）",
        429 => "请求太频繁，稍等再试",
        >= 500 => "对方服务端出错了",
        _ => "调用失败",
    };

    private static JsonObject Fail(string reason)
    {
        var response = new JsonObject { ["ok"] = false, ["reason"] = reason };
        return response;
    }
}
