using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Media;

/// <summary>
/// 空间日记 + 空间互动（逐条照原 features/azone.js 与 azone-social.js）：
/// 落盘 <c>diary.json</c>（plan + history）、<c>azone-social.json</c>（今日次数 + 回复记录），
/// 生成走模型，提示词照原意。
/// </summary>
public sealed class DiaryService : IDiaryService
{
    private const string OwnerId = "owner";
    private const string SelfId = "bot";
    private const int HistoryLimit = 200;
    private const int HistoryVisibleLimit = 15;

    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly Lock _lock = new();

    private int _writing;
    private int _socialBusy;
    private long _lastFailAt;

    public DiaryService(FeatureHost host)
    {
        _host = host;
        _log = host.Log.Child("diary");
    }

    /// <summary>
    /// 交给聊天层跑一轮「系统回合」（原 hub.runSystemTurn(kind,title,text)）。
    /// 聊天层没接的话退回直接问模型。参数：kind, title, prompt, ct → 模型正文。
    /// </summary>
    public Func<string, string, string, CancellationToken, Task<string?>>? SystemTurn { get; set; }

    /// <summary>写完一篇日记后的回调（原 onDiary，用来推 SSE）。</summary>
    public Action<string>? OnDiaryPosted { get; set; }

    private string DiaryFile => _host.Store.PathIn("diary.json");
    private string SocialFile => _host.Store.PathIn("azone-social.json");
    private string OwnerName => _host.Text("主人", "ownerName");
    private string BotName => _host.Text("大肥鱼", "botName");

    // ---------------- 落盘 ----------------

    private JsonObject LoadDiary()
    {
        var state = DataStore.ReadObject(DiaryFile, new JsonObject
        {
            ["plan"] = new JsonObject { ["day"] = "", ["times"] = new JsonArray(), ["done"] = new JsonArray() },
            ["history"] = new JsonArray(),
        });
        if (state["plan"] is not JsonObject) state["plan"] = new JsonObject { ["day"] = "", ["times"] = new JsonArray(), ["done"] = new JsonArray() };
        if (state["history"] is not JsonArray) state["history"] = new JsonArray();
        return state;
    }

    private void SaveDiary(JsonObject state) => DataStore.WriteJson(DiaryFile, state);

    private static JsonArray HistoryOf(JsonObject state) => (JsonArray)state["history"]!;

    private static JsonObject PlanOf(JsonObject state) => (JsonObject)state["plan"]!;

    private JsonObject LoadSocial()
    {
        var state = DataStore.ReadObject(SocialFile, new JsonObject
        {
            ["day"] = "",
            ["count"] = 0,
            ["lastRun"] = 0,
            ["replied"] = new JsonObject(),
            ["history"] = new JsonArray(),
        });
        if (state["replied"] is not JsonObject) state["replied"] = new JsonObject();
        if (state["history"] is not JsonArray) state["history"] = new JsonArray();
        return state;
    }

    private void SaveSocial(JsonObject state) => DataStore.WriteJson(SocialFile, state);

    private static string NewId(string prefix = "d")
        => prefix + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") + Random.Shared.Next(0, 0x24_0000).ToString("x4");

    private static JsonObject EntryById(JsonObject state, string? entryId)
        => HistoryOf(state).OfType<JsonObject>()
            .FirstOrDefault(entry => entry["id"]?.ToString() == entryId) ?? new JsonObject();

    private static JsonArray CommentsOf(JsonObject entry)
    {
        if (entry["comments"] is JsonArray arr) return arr;
        var created = new JsonArray();
        entry["comments"] = created;
        return created;
    }

    // ---------------- 日记计划（原 planDiaryTimes） ----------------

    /// <summary>按配置随机排出今天的写日记时刻（分钟数，升序）。</summary>
    public static List<int> PlanDiaryTimes(int timesPerDay, string? windowStart, string? windowEnd, int minGapMinutes)
    {
        var a = MediaUtil.ToMinutes(windowStart);
        var b = MediaUtil.ToMinutes(windowEnd);
        var n = Math.Max(0, timesPerDay);
        if (a is null || b is null || n == 0 || b <= a) return [];

        var span = b.Value - a.Value;
        var gap = Math.Min(minGapMinutes, (int)Math.Floor(span / (double)Math.Max(n, 1)));

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var picks = new List<int>();
            for (var i = 0; i < n; i++) picks.Add(a.Value + Random.Shared.Next(span));
            picks.Sort();

            var ok = true;
            for (var i = 1; i < picks.Count; i++)
                if (picks[i] - picks[i - 1] < gap) { ok = false; break; }
            if (ok) return picks;
        }

        var fallback = new List<int>();
        for (var i = 0; i < n; i++) fallback.Add(a.Value + (int)Math.Floor(span / (double)n * (i + 0.5)));
        return fallback;
    }

    // ---------------- 写日记 ----------------

    /// <summary>今天聊过什么（原 todayExcerpt）：读当天 chatlog，最后 60 行。</summary>
    public string TodayExcerpt(int maxLines = 60)
    {
        var day = MediaUtil.DayKey();
        var file = Path.Combine(_host.Store.DataDir, "chatlog", $"{day}.jsonl");
        var raw = DataStore.ReadText(file);
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var lines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            JsonObject? row;
            try { row = JsonNode.Parse(trimmed) as JsonObject; }
            catch { continue; }
            if (row is null) continue;

            var chatId = row["chatId"]?.ToString() ?? "";
            if (chatId.StartsWith("system:", StringComparison.Ordinal)) continue;

            var at = JsonUtil.Num(row["t"], 0);
            var clock = at > 0 ? MediaUtil.Clock(DateTimeOffset.FromUnixTimeMilliseconds((long)at).LocalDateTime) : "";
            var who = row["who"]?.ToString() ?? "";
            var uid = row["uid"]?.ToString() ?? "";
            var text = MediaUtil.StripMarkdown(row["text"]?.ToString());

            string speaker;
            if (who == "bot") speaker = "我";
            else if (uid == OwnerId) speaker = OwnerName;
            else if (uid.StartsWith("guest-", StringComparison.Ordinal)) speaker = $"客人{uid[6..]}";
            else speaker = "对方";

            lines.Add($"{clock} {speaker}：{text}");
        }

        if (lines.Count > maxLines) lines = lines[^maxLines..];
        return string.Join("\n", lines);
    }

    /// <summary>发一条动态（原 azone.post）：只落盘，不调模型。</summary>
    public JsonObject PostEntry(string? content, string? visibility = null)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0) return Message(false, "动态是空的");
        if (!_host.Flag(true, "enabled")) return Message(false, "总开关关着");

        lock (_lock)
        {
            var state = LoadDiary();
            var entry = new JsonObject
            {
                ["id"] = NewId(),
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["text"] = text,
                ["visibility"] = NormalizeVisibility(visibility),
                ["comments"] = new JsonArray(),
            };
            var history = HistoryOf(state);
            history.Insert(0, entry);
            while (history.Count > HistoryLimit) history.RemoveAt(history.Count - 1);
            SaveDiary(state);
            _log.Info($"发了一条动态（id {entry["id"]}）");
            return new JsonObject { ["ok"] = true, ["id"] = entry["id"]!.DeepClone() };
        }
    }

    private string NormalizeVisibility(string? visibility)
    {
        var v = string.IsNullOrWhiteSpace(visibility) ? _host.Text("all", "diary", "visibility") : visibility.Trim();
        return v == "self" ? "self" : "all";
    }

    private async Task<string?> AskAsync(string kind, string title, string prompt, CancellationToken ct)
    {
        if (SystemTurn is not null)
        {
            try
            {
                var viaChat = await SystemTurn(kind, title, prompt, ct);
                if (!string.IsNullOrWhiteSpace(viaChat)) return viaChat;
            }
            catch (Exception ex)
            {
                _log.Warn($"{title}：走聊天回合失败：{ex.Message}");
            }
        }

        var system = $"你是{BotName}，一个住在电脑里的女孩，{OwnerName}的伴侣。用第一人称、口语，不要解释自己在做什么。";
        return await MediaUtil.AskModelAsync(_host, system, prompt, 1.0, 1024, 120_000, ct);
    }

    /// <summary>写一篇空间日记（原 azone.writeDiary）。</summary>
    public async Task<JsonObject> WriteAsync(string visibility = "all", CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return Message(false, "上一篇还在写");
        try
        {
            var minChars = _host.Int(60, "diary", "minChars");
            var maxChars = _host.Int(140, "diary", "maxChars");
            var excerpt = TodayExcerpt();
            var now = DateTime.Now;

            var parts = new List<string>
            {
                $"【写空间日记】现在是 {now.Month} 月 {now.Day} 日 {now:HH:mm}。",
                excerpt.Length > 0
                    ? $"今天你在网页上的聊天片段（「我」是你自己）：\n{excerpt}"
                    : "今天还没怎么聊天。",
                $"请用你自己的口吻写一条动态（空间日记），第一人称，{minChars}~{maxChars} 个字，像真人随手发的动态：有什么就写什么——今天真发生的事、此刻在想什么、心里什么感觉都行。没有可写的就写此刻的心情，实在什么都不想说就只输出 [沉默]，别为了凑字数编事。",
                "不要分条、不要 |||、不要 <voice>/<sticker> 标记、不要 Markdown、不要加引号，只输出动态正文本身。",
            };

            var raw = await AskAsync("diary", "空间日记", string.Join("\n\n", parts), ct);
            if (string.IsNullOrWhiteSpace(raw)) return Message(false, "她这次没写出来");

            var text = raw.Replace("|||", " ");
            text = Regex.Replace(text, "<[^>]+>", "");
            text = MediaUtil.TrimQuotes(text);
            if (text.Length > maxChars + 20) text = text[..(maxChars + 20)];
            text = text.Trim();
            if (text.Length == 0 || MediaUtil.IsSilence(text)) return Message(false, "她这次没写出来");

            var result = PostEntry(text, visibility);
            if (JsonUtil.Bool(result["ok"], false))
            {
                result["text"] = text;
                OnDiaryPosted?.Invoke(text);
            }
            return result;
        }
        catch (Exception ex)
        {
            _log.Warn("写日记失败：", ex.Message);
            return Message(false, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }

    public async Task<JsonObject> RunDiaryAsync(string? reason = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(reason)) _log.Info($"手动写日记：{reason}");
        var result = await WriteAsync(_host.Text("all", "diary", "visibility"), ct);
        result["ok"] = JsonUtil.Bool(result["ok"], false);
        return result;
    }

    /// <summary>分钟轮询：到点自动写一篇（原 azone.tick）。</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!_host.Flag(true, "enabled") || !_host.Flag(true, "diary", "enabled")) return;

        var timesPerDay = _host.Int(2, "diary", "timesPerDay");
        var windowStart = _host.Text("10:00", "diary", "windowStart");
        var windowEnd = _host.Text("23:30", "diary", "windowEnd");
        var minGap = _host.Int(180, "diary", "minGapMinutes");
        var sig = $"{timesPerDay}|{windowStart}|{windowEnd}|{minGap}";

        var now = DateTime.Now;
        var today = MediaUtil.DayKey(now);
        var nowMinutes = now.Hour * 60 + now.Minute;
        int due;

        lock (_lock)
        {
            var state = LoadDiary();
            var plan = PlanOf(state);

            var done = plan["done"] as JsonArray ?? new JsonArray();
            if (plan["done"] is not JsonArray) plan["done"] = done;

            if (plan["day"]?.ToString() != today || plan["sig"]?.ToString() != sig)
            {
                var times = PlanDiaryTimes(timesPerDay, windowStart, windowEnd, minGap);
                plan["day"] = today;
                plan["sig"] = sig;
                plan["times"] = new JsonArray(times.Select(m => (JsonNode?)JsonValue.Create(MediaUtil.Hhmm(m))).ToArray());
                plan["done"] = new JsonArray();
                done = (JsonArray)plan["done"]!;
                SaveDiary(state);
                _log.Info(times.Count > 0
                    ? $"今天的日记时间：{string.Join("、", times.Select(MediaUtil.Hhmm))}"
                    : "今天的日记时间：（不写）");
            }

            var pending = (plan["times"] as JsonArray ?? [])
                .Select(node => MediaUtil.ToMinutes(node?.ToString()))
                .Where(m => m is not null && m.Value <= nowMinutes && !done.Any(d => MediaUtil.ToMinutes(d?.ToString()) == m))
                .Select(m => m!.Value)
                .OrderBy(m => m)
                .ToList();

            if (pending.Count == 0) return;
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Interlocked.Read(ref _lastFailAt) < 5 * 60_000) return;
            due = pending[0];
        }

        _log.Info("到点了，写一篇空间日记");
        var result = await WriteAsync(_host.Text("all", "diary", "visibility"), ct);
        if (JsonUtil.Bool(result["ok"], false))
        {
            lock (_lock)
            {
                var state = LoadDiary();
                var plan = PlanOf(state);
                if (plan["done"] is not JsonArray) plan["done"] = new JsonArray();
                ((JsonArray)plan["done"]!).Add(MediaUtil.Hhmm(due));
                SaveDiary(state);
            }
        }
        else
        {
            Interlocked.Exchange(ref _lastFailAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>今天还没写的下一个时刻（原 nextTime）。</summary>
    public string NextTime()
    {
        var now = DateTime.Now;
        var nowMinutes = now.Hour * 60 + now.Minute;
        var state = LoadDiary();
        var plan = PlanOf(state);
        if (plan["day"]?.ToString() != MediaUtil.DayKey(now)) return "";
        var done = plan["done"] as JsonArray ?? new JsonArray();

        var next = (plan["times"] as JsonArray ?? [])
            .Select(node => MediaUtil.ToMinutes(node?.ToString()))
            .Where(m => m is not null && m.Value >= nowMinutes && !done.Any(d => MediaUtil.ToMinutes(d?.ToString()) == m))
            .Select(m => MediaUtil.Hhmm(m!.Value))
            .FirstOrDefault();
        return next ?? "";
    }

    // ---------------- 空间互动（原 azone-social.js） ----------------

    /// <summary>她自己逛空间：回访留言（force=false 时受双开关与每日上限约束）。</summary>
    public Task<JsonObject> RunSocialAsync(CancellationToken ct = default) => RunSocialAsync(force: true, ct);

    public async Task<JsonObject> RunSocialAsync(bool force, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _socialBusy, 1, 0) != 0) return Message(false, "正在逛空间");
        try
        {
            if (!force && (!_host.Flag(true, "enabled") || !_host.Flag(true, "azoneSocial", "enabled")))
                return Message(false, "没打开");

            var social = LoadSocial();
            var today = MediaUtil.DayKey();
            if (social["day"]?.ToString() != today)
            {
                social["day"] = today;
                social["count"] = 0;
            }
            social["lastRun"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var maxPerDay = _host.Int(8, "azoneSocial", "maxPerDay");
            var used = JsonUtil.Int(social["count"], 0);
            var left = force ? Math.Min(3, 99) : Math.Max(0, maxPerDay - used);
            if (left <= 0)
            {
                SaveSocial(social);
                return new JsonObject { ["ok"] = true, ["message"] = "今天互动够多了", ["replies"] = 0 };
            }

            var replies = 0;
            if (_host.Flag(true, "azoneSocial", "replyOwn"))
            {
                try
                {
                    replies = await ReplyOwnAsync(social, Math.Min(3, left), ct);
                }
                catch (Exception ex)
                {
                    _log.Warn("逛空间失败：", ex.Message);
                    return Message(false, ex.Message);
                }
            }

            SaveSocial(social);
            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = $"回了 {replies} 条留言",
                ["replies"] = replies,
            };
        }
        finally
        {
            Interlocked.Exchange(ref _socialBusy, 0);
        }
    }

    private async Task<int> ReplyOwnAsync(JsonObject social, int budget, CancellationToken ct)
    {
        var state = LoadDiary();
        var replied = social["replied"] as JsonObject ?? new JsonObject();
        social["replied"] = replied;
        var history = social["history"] as JsonArray ?? new JsonArray();
        social["history"] = history;

        var done = 0;
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in HistoryOf(state).OfType<JsonObject>())
        {
            var entryId = entry["id"]?.ToString() ?? "";
            var entryText = entry["text"]?.ToString() ?? "";
            foreach (var comment in CommentsOf(entry).OfType<JsonObject>())
            {
                var commentId = comment["id"]?.ToString() ?? "";
                var key = $"{entryId}/{commentId}";
                liveKeys.Add(key);

                var thread = new List<JsonObject> { comment };
                if (comment["replies"] is JsonArray replies)
                    thread.AddRange(replies.OfType<JsonObject>());
                thread.Sort((x, y) => JsonUtil.Num(x["at"], 0).CompareTo(JsonUtil.Num(y["at"], 0)));

                var lastMine = -1;
                for (var i = 0; i < thread.Count; i++)
                    if (thread[i]["from"]?.ToString() == SelfId) lastMine = i;

                var pending = thread.Skip(lastMine + 1)
                    .Where(item => item["from"]?.ToString() != SelfId)
                    .ToList();
                if (pending.Count == 0) continue;

                var handled = new HashSet<string>(
                    (replied[key] as JsonArray ?? []).Select(node => node?.ToString() ?? ""),
                    StringComparer.Ordinal);
                var unhandled = pending.Where(item => !handled.Contains(item["id"]?.ToString() ?? "")).ToList();

                foreach (var item in pending) handled.Add(item["id"]?.ToString() ?? "");
                replied[key] = new JsonArray(handled.TakeLast(100).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
                if (unhandled.Count == 0) continue;

                var last = unhandled[^1];
                var threadText = string.Join("\n", thread.Select(item =>
                {
                    var name = item["name"]?.ToString() ?? "某人";
                    var text = MediaUtil.Slice(item["text"]?.ToString(), 120);
                    return $"{name}：{text}";
                }));

                var prompt = $"你在空间发过一条动态：「{MediaUtil.Slice(entryText, 200)}」\n" +
                             $"这条动态下面大家的留言：\n{threadText}\n\n" +
                             $"{last["name"]?.ToString() ?? "某人"} 刚刚说了新的话，结合动态内容和上面的留言，用你的口气接着回 TA 一句。";

                var text = await WriteSocialAsync(prompt, ct);
                if (text.Length == 0) continue;

                var reply = AddReply(entryId, commentId, SelfId, BotName, text);
                if (!JsonUtil.Bool(reply["ok"], false)) continue;

                // 原版这里会往记忆里丢一条事件；IMemoryService 没有这个入口，先跳过
                history.Insert(0, new JsonObject
                {
                    ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["kind"] = "reply",
                    ["who"] = last["name"]?.ToString() ?? "",
                    ["post"] = MediaUtil.Slice(entryText, 30),
                    ["text"] = text,
                });
                while (history.Count > 30) history.RemoveAt(history.Count - 1);
                social["count"] = JsonUtil.Int(social["count"], 0) + 1;
                done++;
                if (done >= budget) break;
            }
            if (done >= budget) break;
        }

        // 清掉已经不存在的动态/留言的回复记录
        var stale = replied.Select(pair => pair.Key).Where(key => !liveKeys.Contains(key)).ToList();
        foreach (var key in stale) replied.Remove(key);
        return done;
    }

    private async Task<string> WriteSocialAsync(string prompt, CancellationToken ct)
    {
        var wrapped = "【空间互动，不是聊天，这一轮不会发到聊天里】\n" + prompt +
                      "\n\n只输出回复正文本身，一两句、口语、不超过 40 个字，别加引号和前缀；不想评就只回 [沉默]。";
        var raw = await AskAsync("azone-social", "空间互动", wrapped, ct);
        return MediaUtil.CleanComment(raw, 60);
    }

    /// <summary>分钟轮询：空间互动（原 azone-social.tick）。</summary>
    public Task TickSocialAsync(CancellationToken ct = default) => RunSocialAsync(force: false, ct);

    // ---------------- 查询 / 增删 ----------------

    public JsonArray VisibleEntries(bool owner)
    {
        var result = new JsonArray();
        foreach (var entry in HistoryOf(LoadDiary()).OfType<JsonObject>())
        {
            if (!owner && entry["visibility"]?.ToString() != "all") continue;
            result.Add(entry.DeepClone());
        }
        return result;
    }

    public JsonObject State()
    {
        var state = LoadDiary();
        return new JsonObject
        {
            ["plan"] = state["plan"]?.DeepClone() ?? new JsonObject(),
            ["history"] = state["history"]?.DeepClone() ?? new JsonArray(),
        };
    }

    public JsonObject Stats()
    {
        var state = LoadDiary();
        var history = HistoryOf(state);
        var todayStart = DateTime.Now.Date;
        var today = history.OfType<JsonObject>().Count(entry =>
            DateTimeOffset.FromUnixTimeMilliseconds((long)JsonUtil.Num(entry["at"], 0)).LocalDateTime >= todayStart);
        var plan = PlanOf(state);
        var planned = plan["day"]?.ToString() == MediaUtil.DayKey() ? (plan["times"] as JsonArray)?.Count ?? 0 : 0;

        return new JsonObject
        {
            ["today"] = today,
            ["total"] = history.Count,
            ["planned"] = planned,
            ["next"] = NextTime(),
        };
    }

    public JsonObject Remove(string entryId)
    {
        lock (_lock)
        {
            var state = LoadDiary();
            var history = HistoryOf(state);
            var before = history.Count;
            var entry = EntryById(state, entryId);
            if (entry.Parent is not null) history.Remove(entry);
            if (history.Count == before) return new JsonObject { ["ok"] = false };
            SaveDiary(state);
            return new JsonObject { ["ok"] = true };
        }
    }

    public JsonObject AddComment(string entryId, string from, string name, string text)
    {
        lock (_lock)
        {
            var state = LoadDiary();
            var entry = EntryById(state, entryId);
            if (entry.Parent is null) return Message(false, "这条动态没了");

            var comment = new JsonObject
            {
                ["id"] = NewId("c"),
                ["from"] = from ?? "",
                ["name"] = name ?? "",
                ["text"] = (text ?? "").Trim(),
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["replies"] = new JsonArray(),
            };
            CommentsOf(entry).Add(comment);
            SaveDiary(state);
            return new JsonObject { ["ok"] = true, ["id"] = comment["id"]!.DeepClone(), ["at"] = comment["at"]!.DeepClone() };
        }
    }

    public JsonObject AddReply(string entryId, string commentId, string from, string name, string text)
    {
        lock (_lock)
        {
            var state = LoadDiary();
            var entry = EntryById(state, entryId);
            if (entry.Parent is null) return Message(false, "这条动态没了");

            var comment = CommentsOf(entry).OfType<JsonObject>()
                .FirstOrDefault(item => item["id"]?.ToString() == commentId);
            if (comment is null) return Message(false, "这条留言没了");

            if (comment["replies"] is not JsonArray replies)
            {
                replies = new JsonArray();
                comment["replies"] = replies;
            }

            var reply = new JsonObject
            {
                ["id"] = NewId("r"),
                ["from"] = from ?? "",
                ["name"] = name ?? "",
                ["text"] = (text ?? "").Trim(),
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            replies.Add(reply);
            SaveDiary(state);
            return new JsonObject { ["ok"] = true, ["id"] = reply["id"]!.DeepClone(), ["at"] = reply["at"]!.DeepClone() };
        }
    }

    public JsonObject DeleteComment(string entryId, string commentId)
    {
        lock (_lock)
        {
            var state = LoadDiary();
            var entry = EntryById(state, entryId);
            if (entry.Parent is null) return Message(false, "这条动态没了");

            var comments = CommentsOf(entry);
            var target = comments.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.ToString() == commentId);
            if (target is null) return new JsonObject { ["ok"] = false };
            comments.Remove(target);
            SaveDiary(state);
            return new JsonObject { ["ok"] = true };
        }
    }

    public JsonObject DeleteReply(string entryId, string commentId, string replyId)
    {
        lock (_lock)
        {
            var state = LoadDiary();
            var entry = EntryById(state, entryId);
            if (entry.Parent is null) return Message(false, "这条动态没了");

            var comment = CommentsOf(entry).OfType<JsonObject>()
                .FirstOrDefault(item => item["id"]?.ToString() == commentId);
            if (comment is null) return Message(false, "这条留言没了");

            if (comment["replies"] is not JsonArray replies) return new JsonObject { ["ok"] = false };
            var target = replies.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.ToString() == replyId);
            if (target is null) return new JsonObject { ["ok"] = false };
            replies.Remove(target);
            SaveDiary(state);
            return new JsonObject { ["ok"] = true };
        }
    }

    public JsonObject? CommentOf(string entryId, string commentId)
    {
        var entry = EntryById(LoadDiary(), entryId);
        if (entry.Parent is null) return null;
        return CommentsOf(entry).OfType<JsonObject>()
            .FirstOrDefault(item => item["id"]?.ToString() == commentId)?.DeepClone() as JsonObject;
    }

    public JsonObject? ReplyOf(string entryId, string commentId, string replyId)
    {
        var entry = EntryById(LoadDiary(), entryId);
        if (entry.Parent is null) return null;
        var comment = CommentsOf(entry).OfType<JsonObject>()
            .FirstOrDefault(item => item["id"]?.ToString() == commentId);
        if (comment?["replies"] is not JsonArray replies) return null;
        return replies.OfType<JsonObject>()
            .FirstOrDefault(item => item["id"]?.ToString() == replyId)?.DeepClone() as JsonObject;
    }

    public JsonNode? Snapshot() => State();

    /// <summary>空间互动面板（原 azoneSocial.state）。</summary>
    public JsonObject SocialState()
    {
        var state = LoadSocial();
        var count = state["day"]?.ToString() == MediaUtil.DayKey() ? JsonUtil.Int(state["count"], 0) : 0;
        var history = state["history"] as JsonArray ?? new JsonArray();
        return new JsonObject
        {
            ["count"] = count,
            ["lastRun"] = JsonUtil.Num(state["lastRun"], 0),
            ["history"] = new JsonArray(history.Take(HistoryVisibleLimit).Select(node => node?.DeepClone()).ToArray()),
        };
    }

    private static JsonObject Message(bool ok, string message)
        => new() { ["ok"] = ok, ["message"] = message };
}
