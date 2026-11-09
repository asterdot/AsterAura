namespace AuraChat.Core.Ads;

/// <summary>
/// 无广告提供者：用户自带Key或付费模式时使用，不展示任何广告。
/// </summary>
public sealed class NoAdProvider : IAdProvider
{
    public string Name => "NoAd";

    public Task<IReadOnlyList<AdConfig>> LoadAdsAsync(string placement, CancellationToken ct = default)
    {
        // 不加载任何广告
        return Task.FromResult<IReadOnlyList<AdConfig>>(Array.Empty<AdConfig>());
    }

    public Task RecordImpressionAsync(string adId, string placement, CancellationToken ct = default)
    {
        // 无操作
        return Task.CompletedTask;
    }

    public Task RecordClickAsync(string adId, string placement, CancellationToken ct = default)
    {
        // 无操作
        return Task.CompletedTask;
    }
}
