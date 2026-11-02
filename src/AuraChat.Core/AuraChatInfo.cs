namespace AuraChat.Core;

/// <summary>全局常量和版本信息。</summary>
public static class AuraChatInfo
{
    /// <summary>产品名，写入配置默认值 / 网页标题。</summary>
    public const string ProductName = "AuraChat";

    /// <summary>独立版版本号。原插件版是 1.0.0，独立版从 2.0.0 起。</summary>
    public const string Version = "2.0.0";

    /// <summary>HTTP 前缀。原插件挂在 DSH 网页的 /aurachat 下，独立版保留同一前缀以复用网页端。</summary>
    public const string Prefix = "/aurachat";

    /// <summary>主人账号的 subject 固定值。</summary>
    public const string OwnerId = "owner";

    /// <summary>会话有效期：30 天。</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);
}
