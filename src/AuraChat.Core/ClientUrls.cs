namespace AuraChat.Core;

/// <summary>
/// 客户端 API URL 构造（桌面 AppApi / 网页端共用）。
/// 路径协议三端统一：standalone 服务端固定端口按根路径处理 /api/*（ServerHost 拒绝 /aurachat 前缀），
/// Android / Windows / 桌面客户端都拼 base/api/...，这里也一样 —— 不再带旧插件时代的 /aurachat 前缀。
/// 会话凭证只走 Authorization 头或 cookie，这里拼出的 URL 一律不含 token，
/// 避免会话凭证落进访问日志、浏览器历史和 Referer —— 回归测试盯住这一点。
/// </summary>
public static class ClientUrls
{
    /// <summary>拼 API 地址：baseUrl 去尾斜杠 + 根路径 + 相对路径（如 api/state）。</summary>
    public static string Of(string baseUrl, string path)
        => (baseUrl ?? "").TrimEnd('/') + "/" + path.TrimStart('/');

    public static string Media(string baseUrl, string mediaId)
        => Of(baseUrl, "api/media/" + Uri.EscapeDataString(mediaId));

    public static string Sticker(string baseUrl, string md5)
        => Of(baseUrl, "api/sticker/" + Uri.EscapeDataString(md5));

    /// <summary>头像地址。cacheBuster 只用来破缓存（时间戳之类），不是凭证。</summary>
    public static string Avatar(string baseUrl, long cacheBuster)
        => Of(baseUrl, "api/avatar?v=" + cacheBuster.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Background(string baseUrl, long cacheBuster)
        => Of(baseUrl, "api/background?v=" + cacheBuster.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Stream(string baseUrl)
        => Of(baseUrl, "api/stream");

    /// <summary>
    /// 带 Bearer 下载媒体前的安全闸：url 必须与可信 BaseUrl 同源（协议/主机/端口一致），
    /// 路径必须是根路径 /api/ 下的媒体类端点（media/、sticker/、avatar、background），
    /// query 只允许 v=&lt;数字&gt; 缓存戳。Bearer 凭证只发给主人自己填的服务地址，
    /// 绝不跟着消息里构造出来的任意 URL 外流。
    /// </summary>
    public static bool IsTrustedMediaUrl(string? baseUrl, string? url)
    {
        if (!Uri.TryCreate(baseUrl ?? "", UriKind.Absolute, out var baseUri) ||
            !Uri.TryCreate(url ?? "", UriKind.Absolute, out var target))
            return false;
        if (target.Scheme is not ("http" or "https")) return false;
        if (!string.Equals(target.Scheme, baseUri.Scheme, StringComparison.Ordinal)) return false;
        if (!string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (target.Port != baseUri.Port) return false;

        const string prefix = "/api/";
        var path = target.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = path[prefix.Length..];
        var trusted = rest.StartsWith("media/", StringComparison.Ordinal)
            || rest.StartsWith("sticker/", StringComparison.Ordinal)
            || rest is "avatar" or "background";
        if (!trusted) return false;

        var query = target.Query;
        return query.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(query, @"^\?v=\d+$");
    }
}
