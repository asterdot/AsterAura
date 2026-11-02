using System.Text.RegularExpressions;

namespace AuraChat.Features.Media;

/// <summary>
/// 表情搜索（逐条照原 features/stickersrc.js）：抓发表情网的搜索页，用正则把图片地址抠出来。
/// 50 行原实现没有任何库依赖，这里也不用 HTML 解析器。
/// </summary>
public static class StickerSrc
{
    private const string SearchUrl = "https://fabiaoqing.com/search/bqb/keyword/{0}/type/bq/page/1.html";
    private const int TimeoutMs = 15_000;
    private const int MaxBytes = 1024 * 1024;
    private const int PageMaxBytes = 4 * 1024 * 1024;

    /// <summary>页面太大时的文案（原版分开判断：页面 4MB、图片 1MB）。</summary>
    private static readonly Regex ImageTagRegex = new(
        "<img[^>]+(?:data-original|src)=\"(https?://[^\"]+\\.(?:jpg|jpeg|png|gif))\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SkipRegex = new("static/image|/logo|avatar|icon",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>按关键词搜表情，返回图片地址（最多 limit 张）。</summary>
    public static async Task<List<string>> SearchAsync(string? keyword, int limit = 6, CancellationToken ct = default)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) throw new MediaException("没给关键词");

        var url = string.Format(SearchUrl, Uri.EscapeDataString(kw));
        string html;
        try
        {
            var fetched = await MediaUtil.FetchTextAsync(
                url,
                TimeoutMs,
                PageMaxBytes,
                new Dictionary<string, string>
                {
                    ["user-agent"] = MediaUtil.UserAgent124,
                    ["accept"] = "text/html,application/xhtml+xml",
                },
                ct);
            html = fetched.Text;
        }
        catch (MediaException ex) when (ex.Message == "太大了")
        {
            throw new MediaException("页面太大了");
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ImageTagRegex.Matches(html))
        {
            var src = match.Groups[1].Value;
            if (SkipRegex.IsMatch(src)) continue;
            if (!seen.Add(src)) continue;
            results.Add(src);
            if (results.Count >= Math.Max(1, Math.Min(12, limit))) break;
        }

        if (results.Count == 0) throw new MediaException($"没搜到「{kw}」的表情");
        return results;
    }

    /// <summary>单张图片的大小上限（给 web_add_sticker 用）。</summary>
    public const long SingleImageMaxBytes = 5L * 1024 * 1024;
}
