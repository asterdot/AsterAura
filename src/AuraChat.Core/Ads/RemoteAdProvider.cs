using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Core.Ads;

/// <summary>
/// 远程广告提供者：从 Aster Aura 广告服务加载广告，记录展示和点击。
/// </summary>
public sealed class RemoteAdProvider : IAdProvider
{
    private readonly HttpClient _http;
    private readonly string _adServiceUrl;
    private readonly Func<string?> _getAccessToken;

    public string Name => "RemoteAd";

    /// <summary>
    /// 构造远程广告提供者。
    /// </summary>
    /// <param name="http">HTTP 客户端</param>
    /// <param name="adServiceUrl">广告服务基础地址（例如：https://aura.example.com/api/ads）</param>
    /// <param name="getAccessToken">获取当前用户访问令牌的函数</param>
    public RemoteAdProvider(HttpClient http, string adServiceUrl, Func<string?> getAccessToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _adServiceUrl = adServiceUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(adServiceUrl));
        _getAccessToken = getAccessToken ?? throw new ArgumentNullException(nameof(getAccessToken));
    }

    public async Task<IReadOnlyList<AdConfig>> LoadAdsAsync(string placement, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{_adServiceUrl}/load?placement={Uri.EscapeDataString(placement)}");
            
            var token = _getAccessToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Array.Empty<AdConfig>(); // 广告加载失败不阻塞主流程

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonSerializer.Deserialize<JsonNode>(json);
            
            if (node is not JsonObject obj || obj["ads"] is not JsonArray ads)
                return Array.Empty<AdConfig>();

            var result = new List<AdConfig>();
            foreach (var item in ads)
            {
                if (item is not JsonObject ad) continue;
                
                var id = ad["id"]?.ToString();
                var title = ad["title"]?.ToString();
                var description = ad["description"]?.ToString();
                var imageUrl = ad["imageUrl"]?.ToString();
                var clickUrl = ad["clickUrl"]?.ToString();
                var typeStr = ad["type"]?.ToString() ?? "banner";
                var priority = ad["priority"]?.GetValue<int>() ?? 0;

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    continue;

                var type = ParseAdType(typeStr);
                result.Add(new AdConfig(id, title, description, imageUrl, clickUrl, type, priority));
            }

            return result;
        }
        catch
        {
            // 广告加载失败不阻塞主流程
            return Array.Empty<AdConfig>();
        }
    }

    public async Task RecordImpressionAsync(string adId, string placement, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject
            {
                ["adId"] = adId,
                ["placement"] = placement,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var req = new HttpRequestMessage(HttpMethod.Post, $"{_adServiceUrl}/impression")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

            var token = _getAccessToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            // 展示记录失败不阻塞主流程
        }
    }

    public async Task RecordClickAsync(string adId, string placement, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject
            {
                ["adId"] = adId,
                ["placement"] = placement,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var req = new HttpRequestMessage(HttpMethod.Post, $"{_adServiceUrl}/click")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

            var token = _getAccessToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            // 点击记录失败不阻塞主流程
        }
    }

    private static AdType ParseAdType(string typeStr) => typeStr.ToLowerInvariant() switch
    {
        "banner" => AdType.Banner,
        "interstitial" => AdType.Interstitial,
        "native" => AdType.Native,
        "video" => AdType.Video,
        _ => AdType.Banner
    };
}
