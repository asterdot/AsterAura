using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Media;

/// <summary>
/// surf.js 的对齐实现：定时冲一次网（知乎热榜 / 36氪 / B站），按她的兴趣挑话题、
/// 搜资料、整理成一段「看过的笔记」。落盘 interests.json {items,lastPicks}。
/// SelfDriveService 直接调 SurfOnceAsync；面板走 ISurfModule 三件套。
/// </summary>
public sealed class SurfService : ISurfModule
{
    private const int FetchLimit = 512 * 1024;   // FETCH_LIMIT（原 surf.js :14）
    private const int BodyClip = 6000;           // 正文截 6000 字（原 :15）
    private const int MaxInterests = 30;
    private const int MaxPicks = 10;

    private static readonly (string Spot, string Url)[] Spots =
    [
        ("知乎热榜", "https://www.zhihu.com/api/v4/creators/rank/hot?domain=0&limit=10"),
        ("36氪快讯", "https://36kr.com/newsflashes"),
        ("哔哩哔哩", "https://api.bilibili.com/x/web-interface/ranking/v2?main_ver=v3"),
    ];

    private static readonly string[] DefaultInterests = ["追剧进度和更新", "好玩的游戏动态"];

    private static readonly Regex ScriptRegex =
        new("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRegex = new("<[^>]{0,200}>", RegexOptions.Compiled);
    private static readonly Regex TitleRegex =
        new("(?:class=\"[^\"]*title[^\"]*\"[^>]*>|<h[23][^>]*>)(?<t>[^<>]{3,60})<", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpaceRegex = new("\\s{2,}", RegexOptions.Compiled);

    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly string _file;
    private readonly Lock _gate = new();

    public SurfService(FeatureHost host)
    {
        _host = host;
        _log = host.Log.Child("surf");
        _file = host.Store.PathIn("interests.json");
    }

    // ---------------- 落盘 ----------------

    private JsonObject Load()
    {
        lock (_gate)
        {
            var state = DataStore.ReadObject(_file, new JsonObject());
            if (state["items"] is not JsonArray items)
            {
                items = [];
                state["items"] = items;
            }
            if (state["lastPicks"] is not JsonArray) state["lastPicks"] = new JsonArray();

            if (items.Count == 0)
            {
                foreach (var topic in DefaultInterests)
                    items.Add(new JsonObject { ["topic"] = topic, ["why"] = "", ["at"] = 0 });
            }
            return state;
        }
    }

    private void Save(JsonObject state)
    {
        lock (_gate) DataStore.WriteJson(_file, state);
    }

    // ---------------- 兴趣管理（ISurfModule） ----------------

    public JsonArray ListInterests()
    {
        var state = Load();
        lock (_gate) return (JsonArray)(state["items"] as JsonArray ?? []).DeepClone();
    }

    public JsonObject AddInterest(string? topic, string? why)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "兴趣名是空的" };
        if (t.Length > 40) t = t[..40];
        var w = (why ?? "").Trim();
        if (w.Length > 60) w = w[..60];

        lock (_gate)
        {
            var state = Load();
            var items = (JsonArray)state["items"]!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var existing = items.OfType<JsonObject>().FirstOrDefault(i => JsonUtil.Str(i["topic"], "") == t);
            if (existing is not null)
            {
                existing["why"] = w;
                existing["at"] = now;
            }
            else
            {
                items.Add(new JsonObject { ["topic"] = t, ["why"] = w, ["at"] = now });
                while (items.Count > MaxInterests)
                {
                    var oldest = items.OfType<JsonObject>().OrderBy(i => JsonUtil.Num(i["at"], 0)).FirstOrDefault();
                    if (oldest is null) break;
                    items.Remove(oldest);
                }
            }
            Save(state);
            return new JsonObject { ["ok"] = true, ["count"] = items.Count };
        }
    }

    public JsonObject NoteInterest(string? topic, string? note)
    {
        var t = (topic ?? "").Trim();
        var n = (note ?? "").Trim();
        if (t.Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "兴趣名是空的" };
        if (n.Length > 300) n = n[..300];

        lock (_gate)
        {
            var state = Load();
            var items = (JsonArray)state["items"]!;
            var hit = items.OfType<JsonObject>().FirstOrDefault(i => JsonUtil.Str(i["topic"], "") == t);
            if (hit is null) return new JsonObject { ["ok"] = false, ["message"] = "没有这个兴趣，先加一个" };
            hit["note"] = n;
            hit["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Save(state);
            return new JsonObject { ["ok"] = true };
        }
    }

    // ---------------- 冲浪一次 ----------------

    /// <summary>
    /// 冲一次网（原 surf.js surfOnce）：挑来源 → 抓标题 → 模型挑话题 → 搜资料 → 模型整理。
    /// 返回 {ok, topic, digest, message?}。
    /// </summary>
    public async Task<JsonObject> SurfOnceAsync(string? memo, CancellationToken ct = default)
    {
        try
        {
            var spot = PickSpot();
            var titles = await TitlesOfAsync(spot.Url, ct).ConfigureAwait(false);
            if (titles.Count == 0)
            {
                spot = Spots.First(s => s.Spot != spot.Spot); // 这个来源挂了就换一个
                titles = await TitlesOfAsync(spot.Url, ct).ConfigureAwait(false);
            }
            if (titles.Count == 0) return new JsonObject { ["ok"] = false, ["message"] = "两个来源都没抓到内容" };
            RecordPick(spot.Spot);

            var pick = await PickTopicAsync(memo, spot.Spot, titles, ct).ConfigureAwait(false);
            var topic = JsonUtil.Str(pick?["topic"], "").Trim();
            var query = JsonUtil.Str(pick?["query"], "").Trim();
            if (pick is null || pick["skip"] is not null || topic.Length == 0)
            {
                topic = titles[0];
                query = topic;
            }
            if (query.Length == 0) query = topic;

            var body = await SearchBodyAsync(query, ct).ConfigureAwait(false);
            if (body.Trim().Length < 200)
            {
                // 搜索失败退热榜：直接拿热榜标题当材料（原 :220）
                try
                {
                    var (text, _) = await MediaUtil.FetchTextAsync(Spots[0].Url, 15_000, FetchLimit, WebHeaders(), ct).ConfigureAwait(false);
                    var hot = JsonTitles(text, 8);
                    if (hot.Count > 0) body = "热榜标题：\n" + string.Join("\n", hot);
                }
                catch (Exception ex)
                {
                    _log.Debug("热榜兜底也失败：", ex.Message);
                }
            }
            if (body.Trim().Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "没搜到有用的内容" };

            var digest = await DigestAsync(memo, topic, body, ct).ConfigureAwait(false);
            return new JsonObject
            {
                ["ok"] = true,
                ["topic"] = topic,
                ["digest"] = digest,
                ["message"] = RememberLine(digest, topic),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("冲浪失败：", ex.Message);
            return new JsonObject { ["ok"] = false, ["message"] = "冲浪失败：" + ex.Message };
        }
    }

    private async Task<List<string>> TitlesOfAsync(string url, CancellationToken ct)
    {
        try
        {
            var (text, _) = await MediaUtil.FetchTextAsync(url, 15_000, FetchLimit, WebHeaders(), ct).ConfigureAwait(false);
            var titles = JsonTitles(text, 8);
            if (titles.Count == 0) titles = HtmlTitles(text, 8);
            return titles;
        }
        catch (Exception ex)
        {
            _log.Debug("抓取失败：", url, ex.Message);
            return [];
        }
    }

    private async Task<JsonObject?> PickTopicAsync(string? memo, string spot, List<string> titles, CancellationToken ct)
    {
        var interestLines = ListInterests().OfType<JsonObject>()
            .Select(i =>
            {
                var topic = JsonUtil.Str(i["topic"], "");
                var why = JsonUtil.Str(i["why"], "");
                return why.Length > 0 ? $"- {topic}（{why}）" : $"- {topic}";
            })
            .ToList();

        var system =
            "你替她挑一个今天想了解的话题：优先贴合她平时的兴趣和备忘，也要新鲜、有意思。" +
            "只输出 JSON：{\"topic\":\"话题名\",\"query\":\"适合拿去搜索的词\"}；都不感兴趣就输出 {\"skip\":true}。";
        var user = $"她的备忘：{(memo ?? "").Trim()}\n她平时关心：\n{string.Join("\n", interestLines)}\n来源：{spot}\n候选：\n"
            + string.Join("\n", titles.Select((t, i) => $"{i + 1}. {t}"));

        var raw = await MediaUtil.AskModelAsync(_host, system, user, 0.7, 300, 45_000, ct).ConfigureAwait(false);
        return MediaUtil.ExtractJsonObject(raw ?? "");
    }

    private async Task<string> SearchBodyAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&setlang=zh-CN";
            var (text, _) = await MediaUtil.FetchTextAsync(url, 15_000, FetchLimit, WebHeaders(), ct).ConfigureAwait(false);
            return Clip(StripHtml(text), BodyClip);
        }
        catch (Exception ex)
        {
            _log.Debug("搜索失败：", ex.Message);
            return "";
        }
    }

    private async Task<string> DigestAsync(string? memo, string topic, string body, CancellationToken ct)
    {
        var system =
            "你替她把搜到的东西整理成一小段话（150 字以内），像她自己看完随手记的笔记。" +
            "最后一行以「记住了：」开头，写一句她真正要记住的事。";
        var user = $"话题：{topic}\n她的备忘：{(memo ?? "").Trim()}\n材料：\n{body}";
        var raw = await MediaUtil.AskModelAsync(_host, system, user, 0.6, 400, 60_000, ct).ConfigureAwait(false);
        var digest = (raw ?? "").Trim();
        return digest.Length > 0 ? digest : $"{topic}\n记住了：{topic}";
    }

    private static string RememberLine(string digest, string topic)
    {
        foreach (var line in digest.Split('\n').Reverse())
        {
            var s = line.Trim();
            if (s.StartsWith("记住了", StringComparison.Ordinal)) return s;
        }
        return "记住了：" + topic;
    }

    // ---------------- 来源轮换 ----------------

    /// <summary>排除最近 2 次用过的来源，剩下里挑上次用得最久的（原 surf.js :186 pickSpot）。</summary>
    private (string Spot, string Url) PickSpot()
    {
        var picks = Load()["lastPicks"] as JsonArray ?? new JsonArray();
        var recent = picks.OfType<JsonObject>()
            .OrderByDescending(p => JsonUtil.Num(p["at"], 0))
            .Take(2)
            .Select(p => JsonUtil.Str(p["spot"], ""))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var s in Spots)
            if (!recent.Contains(s.Spot)) return s;
        return Spots[0];
    }

    private void RecordPick(string spot)
    {
        lock (_gate)
        {
            var state = Load();
            var picks = (JsonArray)state["lastPicks"]!;
            picks.Add(new JsonObject
            {
                ["spot"] = spot,
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            while (picks.Count > MaxPicks && picks.Count > 0) picks.RemoveAt(0);
            Save(state);
        }
    }

    // ---------------- 抓取小助手 ----------------

    private static IDictionary<string, string> WebHeaders() => new Dictionary<string, string>
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        ["Accept-Language"] = "zh-CN,zh;q=0.9",
    };

    /// <summary>递归抓 title/query/name/word 字段里的短句（原 surf.js jsonTitles）。</summary>
    private static List<string> JsonTitles(string raw, int max)
    {
        var found = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            Walk(doc.RootElement, found);
        }
        catch
        {
            return found;
        }
        return found.Distinct().Take(max).ToList();

        static void Walk(JsonElement el, List<string> found)
        {
            if (found.Count >= 64) return;
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && p.Name is "title" or "query" or "name" or "word")
                    {
                        var s = p.Value.GetString()?.Trim() ?? "";
                        if (s.Length is >= 3 and <= 60) found.Add(s);
                    }
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(p.Value, found);
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item, found);
            }
        }
    }

    private static List<string> HtmlTitles(string html, int max) => TitleRegex
        .Matches(html)
        .Select(m => m.Groups["t"].Value.Trim())
        .Where(t => t.Length is >= 3 and <= 60)
        .Distinct()
        .Take(max)
        .ToList();

    private static string StripHtml(string html)
    {
        var s = ScriptRegex.Replace(html, " ");
        s = TagRegex.Replace(s, " ");
        return SpaceRegex.Replace(s, " ").Trim();
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
