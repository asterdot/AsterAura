namespace AuraChat.Core.Quota;

/// <summary>
/// 无额度服务：用户自带Key模式时使用，不限制额度，不记录消费。
/// </summary>
public sealed class NoQuotaService : IQuotaService
{
    public Task<QuotaBalance> GetBalanceAsync(CancellationToken ct = default)
    {
        // 返回无限额度
        return Task.FromResult(new QuotaBalance(
            Balance: double.PositiveInfinity,
            Currency: "N/A",
            LastUpdated: DateTimeOffset.UtcNow));
    }

    public Task<QuotaDeductionResult> DeductAsync(QuotaDeductionRequest request, CancellationToken ct = default)
    {
        // 不扣费，始终成功
        return Task.FromResult(new QuotaDeductionResult(
            Success: true,
            DeductedAmount: 0,
            RemainingBalance: double.PositiveInfinity));
    }

    public Task<QuotaUsageHistory> GetUsageHistoryAsync(int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        // 返回空历史
        return Task.FromResult(new QuotaUsageHistory(
            Records: Array.Empty<QuotaUsageRecord>(),
            TotalCount: 0));
    }
}
