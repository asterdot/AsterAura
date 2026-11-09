namespace AuraChat.Core.Ads;

/// <summary>
/// 广告提供者接口：统一广告展示、点击追踪和收入记录。
/// 每个实现负责：广告加载、展示逻辑、点击统计、收入计算。
/// </summary>
public interface IAdProvider
{
    /// <summary>提供者名称（用于日志和配置）。</summary>
    string Name { get; }

    /// <summary>
    /// 加载广告配置：从远程服务获取当前可用的广告列表。
    /// </summary>
    /// <param name="placement">广告位置（例如：home/settings/chat）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>广告配置列表</returns>
    Task<IReadOnlyList<AdConfig>> LoadAdsAsync(string placement, CancellationToken ct = default);

    /// <summary>
    /// 记录广告展示：用户看到了广告。
    /// </summary>
    /// <param name="adId">广告ID</param>
    /// <param name="placement">广告位置</param>
    /// <param name="ct">取消令牌</param>
    Task RecordImpressionAsync(string adId, string placement, CancellationToken ct = default);

    /// <summary>
    /// 记录广告点击：用户点击了广告。
    /// </summary>
    /// <param name="adId">广告ID</param>
    /// <param name="placement">广告位置</param>
    /// <param name="ct">取消令牌</param>
    Task RecordClickAsync(string adId, string placement, CancellationToken ct = default);
}

/// <summary>
/// 广告配置：单条广告的展示内容和行为。
/// </summary>
public sealed record AdConfig(
    string Id,
    string Title,
    string? Description,
    string? ImageUrl,
    string? ClickUrl,
    AdType Type,
    int Priority = 0);

/// <summary>
/// 广告类型。
/// </summary>
public enum AdType
{
    /// <summary>横幅广告（Banner）</summary>
    Banner,
    
    /// <summary>插页广告（Interstitial）</summary>
    Interstitial,
    
    /// <summary>原生广告（Native）</summary>
    Native,
    
    /// <summary>视频广告（Rewarded）</summary>
    Video
}
