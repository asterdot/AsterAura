using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Core.Quota;

/// <summary>
/// 远程额度服务：从 Aster Aura 额度服务查询余额和扣费记录。
/// </summary>
public sealed class RemoteQuotaService : IQuotaService
{
    private readonly HttpClient _http;
    private readonly string _quotaServiceUrl;
    private readonly Func<string?> _getAccessToken;

    /// <summary>
    /// 构造远程额度服务。
    /// </summary>
    /// <param name="http">HTTP 客户端</param>
    /// <param name="quotaServiceUrl">额度服务基础地址（例如：https://aura.example.com/api/quota）</param>
    /// <param name="getAccessToken">获取当前用户访问令牌的函数</param>
    public RemoteQuotaService(HttpClient http, string quotaServiceUrl, Func<string?> getAccessToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _quotaServiceUrl = quotaServiceUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(quotaServiceUrl));
        _getAccessToken = getAccessToken ?? throw new ArgumentNullException(nameof(getAccessToken));
    }

    public async Task<QuotaBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_quotaServiceUrl}/balance");
        AddAuthHeader(req);

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var obj = JsonSerializer.Deserialize<JsonObject>(json);

        return new QuotaBalance(
            Balance: obj?["balance"]?.GetValue<double>() ?? 0,
            Currency: obj?["currency"]?.ToString() ?? "USD",
            LastUpdated: DateTimeOffset.UtcNow);
    }

    public async Task<QuotaDeductionResult> DeductAsync(QuotaDeductionRequest request, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["provider"] = request.Provider,
            ["model"] = request.Model,
            ["inputTokens"] = request.InputTokens,
            ["outputTokens"] = request.OutputTokens,
            ["estimatedCost"] = request.EstimatedCost
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_quotaServiceUrl}/deduct")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddAuthHeader(req);

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        
        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new QuotaDeductionResult(
                Success: false,
                DeductedAmount: 0,
                RemainingBalance: 0,
                FailureReason: $"扣费失败（HTTP {(int)resp.StatusCode}）：{error}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var obj = JsonSerializer.Deserialize<JsonObject>(json);

        return new QuotaDeductionResult(
            Success: obj?["success"]?.GetValue<bool>() ?? false,
            DeductedAmount: obj?["deductedAmount"]?.GetValue<double>() ?? 0,
            RemainingBalance: obj?["remainingBalance"]?.GetValue<double>() ?? 0);
    }

    public async Task<QuotaUsageHistory> GetUsageHistoryAsync(int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_quotaServiceUrl}/history?limit={limit}&offset={offset}");
        AddAuthHeader(req);

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var obj = JsonSerializer.Deserialize<JsonObject>(json);

        var records = new List<QuotaUsageRecord>();
        if (obj?["records"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject record) continue;

                var id = record["id"]?.ToString() ?? "";
                var timestampMs = record["timestamp"]?.GetValue<long>() ?? 0;
                var provider = record["provider"]?.ToString() ?? "";
                var model = record["model"]?.ToString() ?? "";
                var inputTokens = record["inputTokens"]?.GetValue<int>() ?? 0;
                var outputTokens = record["outputTokens"]?.GetValue<int>() ?? 0;
                var cost = record["cost"]?.GetValue<double>() ?? 0;

                records.Add(new QuotaUsageRecord(
                    Id: id,
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                    Provider: provider,
                    Model: model,
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    Cost: cost));
            }
        }

        var totalCount = obj?["totalCount"]?.GetValue<int>() ?? records.Count;

        return new QuotaUsageHistory(
            Records: records,
            TotalCount: totalCount);
    }

    private void AddAuthHeader(HttpRequestMessage req)
    {
        var token = _getAccessToken();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
