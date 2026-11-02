using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Life;

/// <summary>
/// 记忆（原 features/memory.js 的整体搬迁）：
/// 落盘 memories.json，条目结构、去重规则、留存衰减、检索打分、上下文拼装都照抄原版。
/// LLM 相关的抽取 / 缩写 / 画像在 MemoryService.Llm.cs。
/// </summary>
public sealed partial class MemoryService : IMemoryService
{
    /// <summary>所有者标识（原 chat/access.js 的 OWNER_ID）。</summary>
    public const string OwnerId = "owner";

    /// <summary>单条记忆正文最长 200 字（原版 slice(0, 200)）。</summary>
    private const int TextMax = 200;

    /// <summary>最多存 3000 条，满了按重要度丢弃。</summary>
    private const int MaxItems = 3000;

    /// <summary>召回日志只留最近 20 次。</summary>
    private const int RecallLogMax = 20;

    private readonly FeatureHost _host;
    private readonly DataStore _store;
    private readonly AreaLog _log;
    private readonly string _file;
    private readonly Lock _gate = new();

    private JsonObject? _state;
    private readonly List<JsonObject> _recallLog = [];
    private MemoryIndex? _index;
    private string _indexStamp = "";
    private int _busy;

    public MemoryService(FeatureHost host)
    {
        _host = host;
        _store = host.Store;
        _log = host.Log.Child("memory");
        _file = host.Store.PathIn("memories.json");
    }

    public string FilePath => _file;

    // ---------------- 落盘 ----------------

    private JsonObject Load()
    {
        lock (_gate)
        {
            if (_state is null)
            {
                _state = DataStore.ReadObject(_file, new JsonObject());
                if (_state["items"] is not JsonArray) _state["items"] = new JsonArray();
            }
            return _state;
        }
    }

    private void Save()
    {
        lock (_gate) DataStore.WriteJson(_file, _state);
    }

    private JsonArray Items()
    {
        var s = Load();
        if (s["items"] is JsonArray arr) return arr;
        arr = new JsonArray();
        s["items"] = arr;
        return arr;
    }

    private JsonObject Map(string key)
    {
        var s = Load();
        if (s[key] is JsonObject obj) return obj;
        obj = new JsonObject();
        s[key] = obj;
        return obj;
    }

    private int Rev => JsonUtil.Int(Load()["rev"], 0);

    private void BumpRev()
    {
        var s = Load();
        s["rev"] = Rev + 1;
        _index = null;
    }

    /// <summary>按 id 找真正的条目节点（改字段要改数组里的那个节点）。</summary>
    private JsonObject? FindItem(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var node in Items())
            if (node is JsonObject m && m["id"]?.ToString() == id) return m;
        return null;
    }

    private MemoryIndex Index()
    {
        var items = Items();
        var last = items.Count > 0 ? (items[^1] as JsonObject)?["id"]?.ToString() ?? "" : "";
        var stamp = $"{items.Count}:{last}:{Rev}";
        var cached = _index;
        if (cached is not null && _indexStamp == stamp) return cached;
        var list = items.OfType<JsonObject>().ToList();
        var idx = MemoryIndex.Build(list);
        _index = idx;
        _indexStamp = stamp;
        return idx;
    }

    // ---------------- 配置 ----------------

    private bool Enabled => _host.Flag(true, "memory", "enabled");

    private int RecallCount => Math.Clamp(_host.Int(6, "memory", "recallCount"), 0, 20);

    // ---------------- 条目增删改 ----------------

    /// <summary>写一条记忆（原 add）：相似度 ≥0.6 且归属不冲突就合并到旧条目。</summary>
    public JsonObject AddInternal(string? text, string about, string aboutName, IEnumerable<string>? tags,
        double importance, string source = "tool", string chatId = "")
    {
        var t = (text ?? "").Trim();
        if (t.Length > TextMax) t = t[..TextMax];
        if (t.Length == 0) return new JsonObject { ["ok"] = false, ["message"] = "内容是空的" };

        lock (_gate)
        {
            Load();
            var items = Items();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var imp = importance == 0 ? 0.5 : importance;

            foreach (var node in items)
            {
                if (node is not JsonObject m) continue;
                if (MemoryText.IsInvalid(m)) continue;
                if (MemoryText.Similarity(MemoryText.Text(m), t) < 0.6) continue;
                if (about.Length > 0 && MemoryText.About(m).Length > 0 && MemoryText.About(m) != about) continue;

                if (t.Length >= MemoryText.Text(m).Length) m["text"] = t;
                m["at"] = now;
                m["importance"] = Math.Max(JsonUtil.Num(m["importance"], 0), JsonUtil.Num(importance, 0));
                BumpRev();
                Save();
                return new JsonObject { ["ok"] = true, ["message"] = "✓", ["updated"] = m["id"]?.ToString() };
            }

            var item = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString()[..8],
                ["text"] = t,
                ["about"] = about,
                ["aboutName"] = aboutName,
                ["tags"] = TagsArray(tags),
                ["importance"] = Math.Clamp(imp, 0, 1),
                ["source"] = source,
                ["chatId"] = chatId,
                ["at"] = now,
                ["hits"] = 0,
            };
            items.Add(item);

            if (items.Count > MaxItems)
            {
                var keep = items.OfType<JsonObject>()
                    .OrderByDescending(m => JsonUtil.Num(m["importance"], 0))
                    .ThenByDescending(m => JsonUtil.Num(m["at"], 0))
                    .Take(MaxItems)
                    .ToList();
                var next = new JsonArray();
                foreach (var m in keep) next.Add(m.DeepClone());
                _state!["items"] = next;
            }

            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true, ["message"] = "✓", ["id"] = item["id"]?.ToString() };
        }
    }

    private static JsonArray TagsArray(IEnumerable<string>? tags)
    {
        var arr = new JsonArray();
        if (tags is null) return arr;
        foreach (var tag in tags.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Take(5))
            arr.Add(tag);
        return arr;
    }

    /// <summary>记一件「经历」（原 addEvent）：source=event，tags 前面加「经历」，重要度 0.45。</summary>
    public JsonObject AddEvent(string? text, string about = "", string aboutName = "", IEnumerable<string>? tags = null,
        double importance = 0.45)
    {
        var all = new List<string> { "经历" };
        if (tags is not null) all.AddRange(tags);
        return AddInternal(text, about, aboutName, all, importance, "event");
    }

    /// <summary>24 小时内的经历（原 recentEvents）。</summary>
    public List<JsonObject> RecentEvents(double hours = 24, int limit = 4)
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(hours * 3_600_000);
        return Items().OfType<JsonObject>()
            .Where(m => MemoryText.Source(m) == "event" && MemoryText.At(m) >= since)
            .OrderByDescending(MemoryText.At)
            .Take(limit)
            .ToList();
    }

    /// <summary>改条目的字段（原 update 的末版：patch 里有什么改什么，然后 rev++）。</summary>
    public JsonObject UpdateInternal(string id, JsonObject patch)
    {
        lock (_gate)
        {
            var m = FindItem(id);
            if (m is null) return new JsonObject { ["ok"] = false, ["message"] = "没有这条" };
            foreach (var key in new[] { "text", "about", "aboutName", "tags", "importance" })
                if (patch[key] is { } value) m[key] = value.DeepClone();
            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true, ["item"] = m.DeepClone() };
        }
    }

    /// <summary>把一条记忆改写成更准确的说法，旧说法留在 history（最多 3 条）。</summary>
    public bool Revise(string id, string? text, double importance)
    {
        var t = (text ?? "").Trim();
        if (t.Length > TextMax) t = t[..TextMax];
        if (t.Length == 0) return false;
        lock (_gate)
        {
            var m = FindItem(id);
            if (m is null) return false;
            var history = m["history"] as JsonArray ?? new JsonArray();
            history.Add(new JsonObject { ["text"] = MemoryText.Text(m), ["at"] = MemoryText.At(m) });
            while (history.Count > 3) history.RemoveAt(0);
            m["history"] = history;
            m["text"] = t;
            m["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            m["importance"] = Math.Max(JsonUtil.Num(m["importance"], 0), JsonUtil.Num(importance, 0));
            BumpRev();
            Save();
            return true;
        }
    }

    /// <summary>标成过时（不删，只是不再参与召回）。</summary>
    public bool Invalidate(string id, string byId = "")
    {
        lock (_gate)
        {
            var m = FindItem(id);
            if (m is null) return false;
            m["invalidAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (byId.Length > 0) m["supersededBy"] = byId;
            BumpRev();
            Save();
            return true;
        }
    }

    public JsonObject Restore(string id)
    {
        lock (_gate)
        {
            var m = FindItem(id);
            if (m is null) return new JsonObject { ["ok"] = false, ["message"] = "没有这条" };
            m.Remove("invalidAt");
            m.Remove("supersededBy");
            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true };
        }
    }

    // ---------------- 召回 ----------------

    /// <summary>召回 + 记录命中（原 recall）。返回的命中带得分与理由。</summary>
    public List<MemoryHit> RecallInternal(string? query, string about, int? limit = null, string where = "",
        string context = "")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var max = limit ?? Math.Max(0, RecallCount);
        var abouts = about.Length > 0 ? new[] { about } : [];

        List<MemoryHit> hits;
        lock (_gate) hits = Index().Search(query, now, abouts, limit: max);

        if (context.Length > 0 && hits.Count < max)
        {
            var have = hits.Select(h => MemoryText.Id(h.Item)).ToHashSet(StringComparer.Ordinal);
            List<MemoryHit> extra;
            lock (_gate) extra = Index().Search(context, now, abouts, minScore: 0.5, limit: max);
            foreach (var h in extra)
            {
                if (hits.Count >= max) break;
                if (have.Contains(MemoryText.Id(h.Item))) continue;
                h.Why.Add("（上一句）");
                hits.Add(h with { Score = h.Score * 0.6 });
            }
        }

        _recallLog.Insert(0, new JsonObject
        {
            ["at"] = now,
            ["where"] = where,
            ["query"] = (query ?? "").Length > 120 ? query![..120] : query ?? "",
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
            {
                ["id"] = MemoryText.Id(h.Item),
                ["text"] = MemoryText.Text(h.Item),
                ["score"] = Math.Round(h.Score, 2),
                ["why"] = new JsonArray(h.Why.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
            }).ToArray()),
        });
        if (_recallLog.Count > RecallLogMax) _recallLog.RemoveRange(RecallLogMax, _recallLog.Count - RecallLogMax);

        if (hits.Count == 0) return hits;

        lock (_gate)
        {
            var today = ChatLog.DayKey();
            foreach (var h in hits)
            {
                var m = FindItem(MemoryText.Id(h.Item));
                if (m is null) continue;
                var lastHit = MemoryText.LastHit(m);
                var lastDay = lastHit > 0
                    ? ChatLog.DayKey(DateTimeOffset.FromUnixTimeMilliseconds(lastHit).LocalDateTime)
                    : "";
                if (lastDay != today) m["hitDays"] = MemoryText.HitDays(m) + 1;
                m["hits"] = JsonUtil.Int(m["hits"], 0) + 1;
                m["lastHit"] = now;
            }
            Save();
        }
        return hits;
    }

    /// <summary>拼进 system prompt 的记忆上下文（原 contextFor）。</summary>
    public string ContextFor(string subject, string userText)
    {
        if (!Enabled) return "";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var parts = new List<string>();
        var key = subject ?? "";

        var profile = key.Length > 0 ? Map("profiles")[key] as JsonObject : null;
        if (profile is not null && MemoryText.Text(profile).Length > 0)
            parts.Add("（你对对方的印象：" + MemoryText.Text(profile) + "）");

        var insights = InsightsOf(key);
        if (insights.Count > 0)
            parts.Add("（和对方相处的经验，照着做就好，不用说出来：" + string.Join('；', insights.Select(x => x["text"]?.ToString() ?? "")) + "）");

        var sty = StyleOf(key);
        var self = StyleOf("self");
        if (sty.Count > 0 || self.Count > 0)
        {
            var bits = new List<string>();
            if (sty.Count > 0) bits.Add("和对方聊久了，" + string.Join('；', sty));
            if (self.Count > 0) bits.Add("你平时不知不觉会用的说法：" + string.Join('、', self));
            parts.Add("（说话方式：" + string.Join('。', bits) + "。自然带一点就好，别刻意模仿，也别变成另一个人。）");
        }

        var recent = RecentEvents();
        if (recent.Count > 0)
            parts.Add("（你最近做过的事："
                      + string.Join('；', recent.Select(m => MemoryText.AgoText(MemoryText.At(m), now) + MemoryText.Text(m)))
                      + "。聊到了可以自然提起。）");

        var q = (userText ?? "").Trim();
        if (q.Length > 0)
        {
            var shown = recent.Select(MemoryText.Id).ToHashSet(StringComparer.Ordinal);
            var hits = RecallInternal(q, key, where: "context", context: q.Length < 12 ? "" : q)
                .Where(h => !shown.Contains(MemoryText.Id(h.Item)))
                .ToList();

            var lines = new List<string>();
            foreach (var h in hits)
            {
                var m = h.Item;
                var grade = GradeOfItem(m);
                if (grade == "drop") continue;
                if (grade == "kw")
                {
                    var kw = m["kw"]?.ToString() ?? "";
                    if (kw.Length > 0) lines.Add($"[{MemoryText.WhenText(MemoryText.At(m), now)}] {Prefix(m)}{kw}");
                    continue;
                }
                if (grade == "brief")
                {
                    var brief = m["brief"]?.ToString() ?? "";
                    if (brief.Length > 0) lines.Add($"[{MemoryText.WhenText(MemoryText.At(m), now)}] {Prefix(m)}{brief}");
                    continue;
                }
                lines.Add(MemoryText.MemoryLine(m, now));
            }

            if (lines.Count > 0)
                parts.Add("（你记得这些事，方括号里是什么时候的事，聊到相关的就自然用上，别把老事当新事说，也别说「根据记忆」："
                          + string.Join('；', lines) + "）");
        }

        return string.Join('\n', parts);
    }

    private static string Prefix(JsonObject m)
    {
        var name = MemoryText.AboutName(m);
        return name.Length > 0 ? $"[{name}] " : "";
    }

    /// <summary>上下文档位：过时的直接 drop；没压过的一律 full。</summary>
    public static string GradeOfItem(JsonObject m)
    {
        if (MemoryText.IsInvalid(m)) return "drop";
        var grade = m["grade"]?.ToString();
        return string.IsNullOrEmpty(grade) ? "full" : grade;
    }

    // ---------------- 画像 / 经验 / 说话方式 ----------------

    public List<JsonObject> InsightsOf(string key)
    {
        var list = Map("insights")[key] as JsonArray;
        return list?.OfType<JsonObject>().ToList() ?? [];
    }

    public List<string> StyleOf(string key)
    {
        var list = Map("styles")[key] as JsonArray;
        return list?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? [];
    }

    public JsonObject? ProfileOf(string who) => Map("profiles")[who] as JsonObject;

    // ---------------- IMemoryService ----------------

    /// <summary>记忆上下文是纯本地拼装，不走模型。</summary>
    public Task<string> RecallAsync(string subject, string userText, CancellationToken ct = default)
        => Task.FromResult(ContextFor(subject, userText));

    /// <summary>一轮对话结束后：即时抽一次事实（写入长期记忆，去重靠 add）。失败只记 warn。</summary>
    public async Task ObserveAsync(string subject, string userText, string botText, CancellationToken ct = default)
    {
        if (!Enabled || !_host.Flag(true, "memory", "liveExtract")) return;
        var user = (userText ?? "").Trim();
        if (user.Length == 0) return;

        ChatLog.Append(_store, subject, subject, user, subject);
        ChatLog.Append(_store, subject, "bot", (botText ?? "").Trim());
        try
        {
            var transcript = $"{subject}：{user}\n我：{(botText ?? "").Trim()}";
            await ExtractFactsAsync(subject, transcript, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("即时提炼记忆失败：", ex.Message);
        }
    }

    public JsonNode? Snapshot() => Stats();

    public JsonObject Stats()
    {
        var items = Items().OfType<JsonObject>().ToList();
        var live = items.Where(m => !MemoryText.IsInvalid(m)).ToList();
        return new JsonObject
        {
            ["count"] = live.Count,
            ["auto"] = live.Count(m => MemoryText.Source(m) == "auto"),
            ["events"] = live.Count(m => MemoryText.Source(m) == "event"),
            ["outdated"] = items.Count - live.Count,
        };
    }

    /// <summary>面板列表：未过时的排前面、新的在前；过时的补上被谁替代。</summary>
    public JsonArray List()
    {
        var items = Items().OfType<JsonObject>().ToList();
        var textOf = items.ToDictionary(m => MemoryText.Id(m), MemoryText.Text, StringComparer.Ordinal);
        var ordered = items
            .OrderBy(m => MemoryText.IsInvalid(m) ? 1 : 0)
            .ThenByDescending(MemoryText.At);

        var output = new JsonArray();
        foreach (var m in ordered)
        {
            var clone = (JsonObject)m.DeepClone();
            var by = m["supersededBy"]?.ToString() ?? "";
            if (by.Length > 0) clone["supersededText"] = textOf.GetValueOrDefault(by, "");
            output.Add(clone);
        }
        return output;
    }

    /// <summary>相处经验：[{key,name,list}]。</summary>
    public JsonArray Insights()
    {
        var names = Map("insightNames");
        var output = new JsonArray();
        foreach (var (key, node) in Map("insights"))
        {
            output.Add(new JsonObject
            {
                ["key"] = key,
                ["name"] = names[key]?.ToString() ?? "",
                ["list"] = node?.DeepClone() ?? new JsonArray(),
            });
        }
        return output;
    }

    public JsonObject Styles() => (JsonObject)Map("styles").DeepClone();

    /// <summary>人物小档案：{ who: {text,name,at} }。</summary>
    public JsonObject Profiles()
    {
        var output = new JsonObject();
        foreach (var (who, node) in Map("profiles"))
        {
            if (node is not JsonObject p) continue;
            output[who] = new JsonObject
            {
                ["text"] = p["text"]?.ToString() ?? "",
                ["name"] = p["name"]?.ToString() ?? "",
                ["at"] = JsonUtil.Num(p["at"], 0),
            };
        }
        return output;
    }

    public JsonArray Recalls()
    {
        var output = new JsonArray();
        foreach (var entry in _recallLog.ToList()) output.Add(entry.DeepClone());
        return output;
    }

    /// <summary>上次整理的时间（yyyy-MM-dd HH:mm；没整理过给空串）。</summary>
    public string LastConsolidate()
    {
        var last = Load()["lastConsolidate"] as JsonObject;
        var at = (long)JsonUtil.Num(last?["at"], 0);
        if (at <= 0) return "";
        return DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    public JsonObject Add(string text, double importance, IEnumerable<string> tags, string source)
        => AddInternal(text, "", "", tags, importance <= 0 ? 0.6 : importance, source);

    public JsonObject Remove(string id)
    {
        lock (_gate)
        {
            var items = Items();
            var hit = items.OfType<JsonObject>().FirstOrDefault(m => MemoryText.Id(m) == id);
            if (hit is not null) items.Remove(hit);
            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true };
        }
    }

    /// <summary>面板改条目：只改传进来的字段（text / importance / tags）。</summary>
    public JsonObject Update(string id, string? text, double? importance, IEnumerable<string>? tags)
    {
        var patch = new JsonObject();
        if (text is not null) patch["text"] = text;
        if (importance is not null) patch["importance"] = Math.Clamp(importance.Value, 0, 1);
        if (tags is not null) patch["tags"] = TagsArray(tags);
        if (patch.Count == 0) return new JsonObject { ["ok"] = true, ["message"] = "没改什么" };
        return UpdateInternal(id, patch);
    }

    public JsonObject SetProfile(string about, string text, string name)
    {
        lock (_gate)
        {
            var profiles = Map("profiles");
            var t = (text ?? "").Trim();
            if (t.Length > TextMax) t = t[..TextMax];
            if (t.Length > 0)
            {
                var oldName = (profiles[about] as JsonObject)?["name"]?.ToString() ?? "";
                profiles[about] = new JsonObject
                {
                    ["text"] = t,
                    ["name"] = name.Length > 0 ? name : oldName,
                    ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
            }
            else
            {
                profiles.Remove(about);
            }
            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true };
        }
    }

    public JsonObject SetStyle(string who, JsonArray? lines)
    {
        var key = string.IsNullOrWhiteSpace(who) ? "self" : who.Trim();
        var list = ReadLines(lines);
        lock (_gate)
        {
            Map("styles")[key] = new JsonArray(list
                .Select(t => (JsonNode)JsonValue.Create(t.Length > 40 ? t[..40] : t)!)
                .Take(key == "self" ? 6 : 5)
                .ToArray());
            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true };
        }
    }

    public JsonObject SetInsights(string key, JsonArray? lines, string name)
    {
        var list = ReadLines(lines);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var arr = new JsonArray();
        foreach (var text in list.Take(8))
        {
            var t = text.Length > 40 ? text[..40] : text;
            arr.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString()[..8],
                ["text"] = t,
                ["at"] = now,
            });
        }
        lock (_gate)
        {
            Map("insights")[key] = arr;
            if (name.Length > 0) Map("insightNames")[key] = name;
            BumpRev();
            Save();
            return new JsonObject { ["ok"] = true, ["count"] = arr.Count };
        }
    }

    private static List<string> ReadLines(JsonArray? lines)
    {
        var output = new List<string>();
        if (lines is null) return output;
        foreach (var node in lines)
        {
            var s = node is JsonValue v && v.TryGetValue<string>(out var str)
                ? str
                : (node as JsonObject)?["text"]?.ToString() ?? "";
            s = s.Trim();
            if (s.Length > 0) output.Add(s);
        }
        return output;
    }
}
