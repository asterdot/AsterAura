using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AuraChat.Features.Life;

/// <summary>一条检索命中：命中的记忆、得分、以及为什么命中（供调试面板看）。</summary>
public sealed record MemoryHit(JsonObject Item, double Score, List<string> Why);

/// <summary>
/// 记忆检索索引：BM25 变体（k1=1.4、b=0.7）。
/// 逐条对齐原 features/memory.js 的 tokenize / buildIndex / search。
/// </summary>
public sealed class MemoryIndex
{
    private const double K1 = 1.4;
    private const double B = 0.7;

    public sealed record Doc(JsonObject Item, Dictionary<string, int> Tf, int Len);

    private MemoryIndex(List<Doc> docs, Dictionary<string, int> df, double avgLen)
    {
        Docs = docs;
        Df = df;
        AvgLen = avgLen;
    }

    public List<Doc> Docs { get; }
    public Dictionary<string, int> Df { get; }
    public double AvgLen { get; }
    public int N => Docs.Count;

    public static MemoryIndex Build(IReadOnlyList<JsonObject> items)
    {
        var df = new Dictionary<string, int>();
        var docs = new List<Doc>(items.Count);
        foreach (var it in items)
        {
            var text = $"{MemoryText.Text(it)} {string.Join(' ', MemoryText.Tags(it))} {MemoryText.AboutName(it)}";
            var toks = MemoryText.Tokenize(text);
            var tf = new Dictionary<string, int>();
            foreach (var t in toks) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys) df[t] = df.GetValueOrDefault(t) + 1;
            docs.Add(new Doc(it, tf, toks.Count == 0 ? 1 : toks.Count));
        }
        var avgLen = docs.Sum(d => d.Len) / (double)(docs.Count == 0 ? 1 : docs.Count);
        return new MemoryIndex(docs, df, avgLen);
    }

    /// <param name="abouts">关于谁的命中加权（原 aboutHit：+0.8）。</param>
    public List<MemoryHit> Search(
        string? query,
        long now,
        IReadOnlyCollection<string>? abouts = null,
        bool includeInvalid = false,
        double minScore = 0.35,
        int limit = 6)
    {
        var q = MemoryText.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        var aboutSet = new HashSet<string>(abouts ?? []);
        var scored = new List<MemoryHit>();

        foreach (var d in Docs)
        {
            var s = 0.0;
            var why = new List<(string Token, double Part)>();
            foreach (var t in q)
            {
                var f = d.Tf.GetValueOrDefault(t);
                if (f == 0) continue;
                var n = Df.GetValueOrDefault(t);
                var idf = Math.Log(1 + (N - n + 0.5) / (n + 0.5));
                var part = idf * (f * (K1 + 1)) / (f + K1 * (1 - B + B * d.Len / AvgLen));
                s += part;
                why.Add((t, part));
            }

            var it = d.Item;
            if (MemoryText.IsInvalid(it) && !includeInvalid) continue;
            var aboutHit = MemoryText.About(it).Length > 0 && aboutSet.Contains(MemoryText.About(it));
            if (s <= 0 && !aboutHit) continue;

            var importance = 0.6 + 0.8 * MemoryText.Importance(it);
            var score = (s + (aboutHit ? 0.8 : 0)) * importance * (0.55 + 0.45 * MemoryText.Retention(it, now));

            var reasons = why.OrderByDescending(w => w.Part).Take(3).Select(w => w.Token).ToList();
            if (aboutHit) reasons.Add("（关于这个人）");
            scored.Add(new MemoryHit(it, score, reasons));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Where(x => x.Score >= minScore).Take(limit).ToList();
    }
}

/// <summary>
/// 记忆里用到的文本处理与时间格式化（原 memory.js 顶部的导出函数）。
/// </summary>
public static class MemoryText
{
    private static readonly HashSet<string> Stop =
        [.. "的了是我你他她它们在有和就不也都很吗吧呢啊呀哦嗯这那个一上下来去说要会能着过".Select(c => c.ToString())];

    private static readonly Regex WordRe = new(@"[a-z0-9_]{2,}|[\u4E00-\u9FFF]+", RegexOptions.Compiled);
    private static readonly Regex AsciiWordRe = new(@"^[a-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>英文按词、中文按二元组切分；单字非停用词保留。</summary>
    public static List<string> Tokenize(string? text)
    {
        var output = new List<string>();
        var s = (text ?? "").ToLowerInvariant();
        foreach (Match m in WordRe.Matches(s))
        {
            var w = m.Value;
            if (AsciiWordRe.IsMatch(w))
            {
                output.Add(w);
                continue;
            }
            if (w.Length == 1)
            {
                if (!Stop.Contains(w)) output.Add(w);
                continue;
            }
            for (var i = 0; i < w.Length - 1; i++) output.Add(w.Substring(i, 2));
        }
        return output;
    }

    /// <summary>词集合 Jaccard。</summary>
    public static double Similarity(string? a, string? b)
    {
        var setA = new HashSet<string>(Tokenize(a));
        var setB = new HashSet<string>(Tokenize(b));
        if (setA.Count == 0 || setB.Count == 0) return 0;
        var inter = setA.Count(setB.Contains);
        return inter / (double)(setA.Count + setB.Count - inter);
    }

    public static string AgoText(long at, long now)
    {
        var m = Math.Max(0, (long)Math.Round((now - at) / 60000.0));
        if (m < 30) return "刚才";
        if (m < 60 * 20) return $"{Math.Max(1, (long)Math.Round(m / 60.0))} 小时前";
        return "昨天";
    }

    public static string WhenText(long at, long now)
    {
        if (at <= 0) return "";
        var d = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime;
        var n = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime;
        var day0 = (DateTime x) => new DateTime(x.Year, x.Month, x.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var days = (int)Math.Round((day0(n) - day0(d)).TotalDays);
        var md = $"{d.Month:00}-{d.Day:00}";
        if (days <= 0) return "今天";
        if (days == 1) return "昨天";
        if (days < 7) return $"{days} 天前";
        if (d.Year == n.Year) return md;
        if (d.Year == n.Year - 1) return "去年 " + md;
        return $"{d.Year}-{md}";
    }

    public static string MemoryLine(JsonObject item, long now)
        => $"[{WhenText(MemoryText.At(item), now)}] " + (AboutName(item).Length > 0 ? $"[{AboutName(item)}] " : "") + Text(item);

    /// <summary>衰减后的留存率：越久没被想起、重要度越低，掉得越快。</summary>
    public static double Retention(JsonObject m, long now)
    {
        var last = Math.Max(At(m), LastHit(m));
        var days = Math.Max(0, (now - last) / 86400_000.0);
        var strength = 1 + Math.Min(10, HitDays(m));
        var tau = 20 * strength * (0.5 + Importance(m));
        return Math.Exp(-days / tau);
    }

    /// <summary>该不该忘：过时的留 180 天；非自动来源永不遗忘；重要的不遗忘。</summary>
    public static bool ShouldForget(JsonObject m, long now)
    {
        var invalidAt = InvalidAt(m);
        if (invalidAt > 0) return now - invalidAt > 180L * 86400_000;
        if (Source(m) != "auto") return false;
        if (Importance(m) >= 0.6) return false;
        return Retention(m, now) < 0.05;
    }

    /// <summary>从模型输出里抠出 {insights, style}；先试对象再试数组。</summary>
    public static (List<string> Insights, List<string> Style) ParseReflection(string? output)
    {
        var text = output ?? "";
        var objMatch = Regex.Match(text, @"\{[\s\S]*\}");
        if (objMatch.Success)
        {
            try
            {
                if (JsonNode.Parse(objMatch.Value) is JsonObject j
                    && (j["insights"] is JsonArray || j["style"] is JsonArray))
                {
                    return (StringList(j["insights"]), StringList(j["style"]));
                }
            }
            catch
            {
                // 落到下面的数组分支
            }
        }

        var arrMatch = Regex.Match(text, @"\[[\s\S]*\]");
        if (arrMatch.Success)
        {
            try
            {
                if (JsonNode.Parse(arrMatch.Value) is JsonArray arr) return (StringList(arr), []);
            }
            catch
            {
                // 模型输出不可解析时当作没写
            }
        }
        return ([], []);
    }

    /// <summary>JSON 数组项转字符串：字符串原样，对象取 text 字段。</summary>
    public static List<string> StringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            var s = item is JsonValue v && v.TryGetValue<string>(out var str)
                ? str
                : (item as JsonObject)?["text"]?.ToString() ?? "";
            s = s.Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    // ---- 条目字段读取（原版全部靠对象动态属性，这里统一收口，未知字段原样保留）----

    public static string Text(JsonObject o) => o["text"]?.ToString() ?? "";
    public static string About(JsonObject o) => o["about"]?.ToString() ?? "";
    public static string AboutName(JsonObject o) => o["aboutName"]?.ToString() ?? "";
    public static string Source(JsonObject o) => o["source"]?.ToString() ?? "";
    public static string Id(JsonObject o) => o["id"]?.ToString() ?? "";

    public static double Importance(JsonObject o) => AuraChat.Core.JsonUtil.Num(o["importance"], 0.5);
    public static long At(JsonObject o) => (long)AuraChat.Core.JsonUtil.Num(o["at"], 0);
    public static long LastHit(JsonObject o) => (long)AuraChat.Core.JsonUtil.Num(o["lastHit"], 0);
    public static long InvalidAt(JsonObject o) => (long)AuraChat.Core.JsonUtil.Num(o["invalidAt"], 0);
    public static int HitDays(JsonObject o) => AuraChat.Core.JsonUtil.Int(o["hitDays"], 0);
    public static bool IsInvalid(JsonObject o) => InvalidAt(o) > 0;

    public static List<string> Tags(JsonObject o)
    {
        var list = new List<string>();
        if (o["tags"] is JsonArray arr)
            foreach (var t in arr)
                list.Add(t?.ToString() ?? "");
        return list;
    }
}
