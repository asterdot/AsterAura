using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Life;

/// <summary>
/// 缺口池：她心里挂着、但还没弄明白的事。
/// 三条来源：她自己说「不知道/不清楚」、工具调用没查到、这一轮请求失败。
/// 张力按时间自己涨，被解决或被聊到才降；张力数字永远不进对话文本。
/// 逐条对齐原 features/gaps.js（默认值、去重阈值、张力公式都不改）。
/// </summary>
public sealed class GapPool
{
    public const double BaseTensionDefault = 80;
    public const double TensionPerHourDefault = 5;
    public const double MaxTensionDefault = 200;
    public const double DedupeThresholdDefault = 0.6;
    public const double DedupeBumpDefault = 10;
    public const int MaxItemsDefault = 60;

    /// <summary>她自己说了这几句，就说明这事没解决。</summary>
    private static readonly string[] UnresolvedWords =
        ["不知道", "无法回答", "不清楚", "不太清楚", "说不上来", "i don't know", "idontknow"];

    private readonly DataStore _store;
    private readonly Func<JsonObject> _config;
    private readonly AreaLog _log;
    private readonly string _file;
    private readonly Lock _gate = new();
    private JsonObject? _state;

    public GapPool(DataStore store, Func<JsonObject> config, AreaLog log)
    {
        _store = store;
        _config = config;
        _log = log;
        _file = store.PathIn("gaps.json");
    }

    public string FilePath => _file;

    /// <summary>配置覆盖默认值（原 gap 的 cfgOf）。</summary>
    private JsonObject Cfg()
    {
        var output = new JsonObject
        {
            ["enabled"] = true,
            ["baseTension"] = BaseTensionDefault,
            ["tensionPerHour"] = TensionPerHourDefault,
            ["maxTension"] = MaxTensionDefault,
            ["dedupeThreshold"] = DedupeThresholdDefault,
            ["dedupeBump"] = DedupeBumpDefault,
            ["maxItems"] = MaxItemsDefault,
        };
        if (_config()["gaps"] is JsonObject overrides)
            foreach (var (key, value) in overrides)
                output[key] = value?.DeepClone();
        return output;
    }

    private JsonObject Load()
    {
        lock (_gate)
        {
            return _state ??= DataStore.ReadObject(_file, new JsonObject { ["items"] = new JsonArray() });
        }
    }

    private void Save()
    {
        lock (_gate) DataStore.WriteJson(_file, _state);
    }

    private static JsonArray ItemsOf(JsonObject state)
    {
        if (state["items"] is JsonArray arr) return arr;
        arr = new JsonArray();
        state["items"] = arr;
        return arr;
    }

    /// <summary>张力由创建时间派生，不累加——重启、改配置都不会漂。</summary>
    public double TensionOf(JsonObject item, long now)
    {
        var c = Cfg();
        var created = (long)JsonUtil.Num(item["createdAt"], now);
        var hours = Math.Max(0, (now - created) / 3_600_000.0);
        var tension = JsonUtil.Num(c["baseTension"], BaseTensionDefault)
                      + hours * JsonUtil.Num(c["tensionPerHour"], TensionPerHourDefault)
                      - JsonUtil.Num(item["relief"], 0);
        return Math.Clamp(tension, 0, JsonUtil.Num(c["maxTension"], MaxTensionDefault));
    }

    public bool Enabled => JsonUtil.Bool(Cfg()["enabled"], true);

    /// <summary>还没解决、张力还大于 0 的（文件顺序 = 新→旧）。</summary>
    public List<JsonObject> Open(long now)
    {
        var output = new List<JsonObject>();
        foreach (var node in ItemsOf(Load()))
        {
            if (node is not JsonObject g) continue;
            if ((long)JsonUtil.Num(g["resolvedAt"], 0) > 0) continue;
            if (TensionOf(g, now) <= 0) continue;
            output.Add(g);
        }
        return output;
    }

    /// <summary>张力最高、最该提的那一件（附 tension）。</summary>
    public JsonObject? Top(long now)
    {
        var list = Open(now);
        if (list.Count == 0) return null;
        var best = list[0];
        foreach (var g in list)
            if (TensionOf(g, now) > TensionOf(best, now)) best = g;
        var clone = (JsonObject)best.DeepClone();
        clone["tension"] = TensionOf(best, now);
        return clone;
    }

    /// <summary>记下一个没解决的事；同义（相似度 ≥ dedupeThreshold）只给老那条加张力。</summary>
    public JsonObject? Add(string? text, string source = "model")
    {
        var t = (text ?? "").Trim();
        if (t.Length > 120) t = t[..120];
        if (t.Length == 0) return null;

        var c = Cfg();
        if (!JsonUtil.Bool(c["enabled"], true)) return null;
        var threshold = JsonUtil.Num(c["dedupeThreshold"], DedupeThresholdDefault);
        var bump = JsonUtil.Num(c["dedupeBump"], DedupeBumpDefault);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            Load();
            var items = ItemsOf(_state!);
            foreach (var node in items)
            {
                if (node is not JsonObject g) continue;
                if ((long)JsonUtil.Num(g["resolvedAt"], 0) > 0) continue;
                if (TextSimilarity(g["text"]?.ToString() ?? "", t) < threshold) continue;
                g["relief"] = Math.Max(0, JsonUtil.Num(g["relief"], 0) - bump);
                g["lastHitAt"] = now;
                Save();
                _log.Debug($"缺口去重命中：{g["text"]}");
                return (JsonObject)g.DeepClone();
            }

            var item = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["text"] = t,
                ["source"] = source,
                ["createdAt"] = now,
                ["relief"] = 0,
                ["resolvedAt"] = 0,
            };
            var maxItems = Math.Max(1, JsonUtil.Int(c["maxItems"], MaxItemsDefault));
            var next = new JsonArray { item };
            foreach (var old in items)
            {
                if (next.Count >= maxItems) break;
                next.Add(old?.DeepClone());
            }
            _state!["items"] = next;
            Save();
            _log.Info($"记下一个没解决的事：{t}");
            return (JsonObject)item.DeepClone();
        }
    }

    /// <summary>聊到了就算缓解（不是彻底解决）。</summary>
    public JsonObject? Relieve(string? id, double amount = 40)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            var items = ItemsOf(Load());
            foreach (var node in items)
            {
                if (node is not JsonObject g || g["id"]?.ToString() != id) continue;
                if ((long)JsonUtil.Num(g["resolvedAt"], 0) > 0) return null;
                g["relief"] = JsonUtil.Num(g["relief"], 0) + amount;
                g["lastTalkedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Save();
                return (JsonObject)g.DeepClone();
            }
            return null;
        }
    }

    /// <summary>彻底划掉。</summary>
    public JsonObject? Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            var items = ItemsOf(Load());
            foreach (var node in items)
            {
                if (node is not JsonObject g || g["id"]?.ToString() != id) continue;
                g["resolvedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Save();
                return (JsonObject)g.DeepClone();
            }
            return null;
        }
    }

    /// <summary>按「那件事本身」找（不用一字不差），找到就划掉。</summary>
    public JsonObject? ResolveByText(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hit = Open(now).FirstOrDefault(g =>
        {
            var gText = g["text"]?.ToString() ?? "";
            return gText == t || gText.Contains(t, StringComparison.Ordinal) || t.Contains(gText, StringComparison.Ordinal);
        });
        return hit is null ? null : Resolve(hit["id"]?.ToString());
    }

    /// <summary>只给「心里挂着什么」，不给「你现在有多急」。</summary>
    public string ContextLine(long now)
    {
        var c = Cfg();
        if (!JsonUtil.Bool(c["enabled"], true)) return "";
        var baseTension = JsonUtil.Num(c["baseTension"], BaseTensionDefault);
        var picks = Open(now)
            .Select(g => (Item: g, Tension: TensionOf(g, now)))
            .Where(x => x.Tension >= baseTension)
            .OrderByDescending(x => x.Tension)
            .Take(2)
            .Select(x => $"（你心里还挂着一件事：{x.Item["text"]}）");
        return string.Concat(picks);
    }

    /// <summary>给面板看的列表（附 tension / resolved）。</summary>
    public JsonArray List()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var output = new JsonArray();
        foreach (var node in ItemsOf(Load()))
        {
            if (node is not JsonObject g) continue;
            var clone = (JsonObject)g.DeepClone();
            clone["tension"] = TensionOf(g, now);
            clone["resolved"] = (long)JsonUtil.Num(g["resolvedAt"], 0) > 0;
            output.Add(clone);
        }
        return output;
    }

    public void Clear()
    {
        lock (_gate)
        {
            Load();
            _state!["items"] = new JsonArray();
            Save();
        }
    }

    /// <summary>她自己说了这几句，就说明这事没解决。</summary>
    public static bool LooksUnresolved(string? reply)
    {
        var t = reply ?? "";
        return UnresolvedWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 中文二元组 Jaccard + 最长公共子序列覆盖率，取大的那个。
    /// 只用 Jaccard 抓不住「秦始皇到底是怎么死的／秦始皇怎么死的啊」这种，LCS 能；
    /// 反过来 LCS 抓不住语序打乱的，Jaccard 能。
    /// </summary>
    public static double TextSimilarity(string? a, string? b)
    {
        var s1 = (a ?? "").Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        var s2 = (b ?? "").Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
        if (s1.Length == 0 || s2.Length == 0) return 0;
        if (s1 == s2) return 1;
        if (s1.Contains(s2, StringComparison.Ordinal) || s2.Contains(s1, StringComparison.Ordinal))
            return (double)Math.Min(s1.Length, s2.Length) / Math.Max(s1.Length, s2.Length);

        static HashSet<string> Grams(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < s.Length - 1; i++) set.Add(s.Substring(i, 2));
            return set;
        }

        var g1 = Grams(s1);
        var g2 = Grams(s2);
        var hit = g1.Count(g2.Contains);
        var union = g1.Count + g2.Count - hit;
        var jaccard = union == 0 ? 0 : (double)hit / union;
        // 超过 80 字的长句 LCS 没意义（随便两个长句都能凑出高覆盖），只对短句启用。
        var lcs = Math.Max(s1.Length, s2.Length) <= 80 ? LcsRatio(s1, s2) : 0;
        return Math.Max(jaccard, lcs);
    }

    private static double LcsRatio(string s1, string s2)
    {
        var n = s1.Length;
        var m = s2.Length;
        if (n == 0 || m == 0) return 0;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
                cur[j] = s1[i - 1] == s2[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return (double)prev[m] / Math.Max(n, m);
    }
}
