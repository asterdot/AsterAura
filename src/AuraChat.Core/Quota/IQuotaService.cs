namespace AuraChat.Core.Quota;

/// <summary>
/// 额度服务接口：管理用户模型调用额度的查询、扣费和充值。
/// </summary>
public interface IQuotaService
{
    /// <summary>
    /// 查询当前用户余额。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>余额信息</returns>
    Task<QuotaBalance> GetBalanceAsync(CancellationToken ct = default);

    /// <summary>
    /// 扣除额度：模型调用完成后记录消费。
    /// </summary>
    /// <param name="request">扣费请求</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>扣费结果</returns>
    Task<QuotaDeductionResult> DeductAsync(QuotaDeductionRequest request, CancellationToken ct = default);

    /// <summary>
    /// 查询消费记录：用户查看自己的额度使用历史。
    /// </summary>
    /// <param name="limit">返回记录数量</param>
    /// <param name="offset">偏移量（分页）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>消费记录列表</returns>
    Task<QuotaUsageHistory> GetUsageHistoryAsync(int limit = 20, int offset = 0, CancellationToken ct = default);
}

/// <summary>
/// 余额信息。
/// </summary>
public sealed record QuotaBalance(
    double Balance,
    string Currency,
    DateTimeOffset LastUpdated);

/// <summary>
/// 扣费请求。
/// </summary>
public sealed record QuotaDeductionRequest(
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    double EstimatedCost);

/// <summary>
/// 扣费结果。
/// </summary>
public sealed record QuotaDeductionResult(
    bool Success,
    double DeductedAmount,
    double RemainingBalance,
    string? FailureReason = null);

/// <summary>
/// 消费历史记录。
/// </summary>
public sealed record QuotaUsageHistory(
    IReadOnlyList<QuotaUsageRecord> Records,
    int TotalCount);

/// <summary>
/// 单条消费记录。
/// </summary>
public sealed record QuotaUsageRecord(
    string Id,
    DateTimeOffset Timestamp,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    double Cost);
