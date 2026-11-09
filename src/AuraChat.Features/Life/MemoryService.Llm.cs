using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Auth;
using AuraChat.Core.Runtime;
using AuraChat.Features.Media;

namespace AuraChat.Features.Life;

/// <summary>
/// memory.js 的巩固半边（partial MemoryService）：
/// - ExtractFactsAsync：LLM 即时/批量提炼事实（聊天 12 条 / 日记 6 条上限）。
/// - ConsolidateAsync：按天消费 chatlog（processed 偏移、80 行分块）、遗忘、建档、总结经验、压缩分级。
/// - TickAsync：每小时看一眼 consolidateTimes / compressEveryHours。
/// </summary>
public sealed partial class MemoryService
{
    /// <summary>聊天提炼一次最多 12 条（原 memory.js :499）。</summary>
    private const int ExtractChatMax = 12;

    /// <summary>日记（azone）提炼一次最多 6 条（原 :503）。</summary>
    private const int ExtractDiaryMax = 6;

    /// <summary>手工导入一批最多 80 条（原 :507）。</summary>
    internal const int ExtractImportMax = 80;

    /// <summary>巩固时 chatlog 每 80 行一块（原 :446）。</summary>
    private const int ChunkLines = 80;

    /// <summary>压缩一轮最多处理 40 条（原 :715）。</summary>
    private const int CompressBatch = 40;

    /// <summary>人物档案门槛：同一个 about 至少 3 条记忆才建档（原 :355）。</summary>
    private const int ProfileMinItems = 3;

    private const long DayMs = 86_400_000;

    /// <summary>提炼结果特殊值：模型没配/调用失败/空响应。调用方不得因此推进巩固偏移。</summary>
    private const int ModelUnavailable = -1;

    private const string ExtractSystem =
        "你从聊天记录里提炼值得长期记住的事实（关于说话的人、被提到的人或事）。" +
        "只输出 JSON 数组，元素形如 {\"text\":\"一句话\",\"importance\":0.5,\"tags\":[\"标签\"],\"about\":\"归属的人，没有就空字符串\"}。" +
        "规则：只记稳定有用的事（身份、关系、偏好、约定、重要经历），寒暄、过程、闲聊不记；" +
        "每条一句话、不超过 60 字；最多 {max} 条；没有值得记的就输出 []。";

    private const string ProfileSystem =
        "你是记忆助手。下面是关于同一个人的多条记忆，把它们整合成一段人物小档案" +
        "（性格、喜好、习惯、关系里重要的事）。只输出 JSON：{\"text\":\"不超过200字\",\"name\":\"这个人的称呼\"}；" +
        "实在没什么可写的输出 {\"text\":\"\"}。";

    private const string ReflectSystem =
        "你是记忆助手。根据这些记忆，总结两样东西：" +
        "「insights」= 和主人相处的经验（他喜欢什么、讨厌什么、相处要注意什么），不超过 8 条；" +
        "「style」= 说话的人自己的语气习惯，不超过 6 条。每条不超过 40 字。" +
        "只输出 JSON：{\"insights\":[\"…\"],\"style\":[\"…\"]}；没什么可总结就输出 {}。";

    private const string CompressSystem =
        "你在帮忙压缩长期记忆。把每条记忆缩写成不超过 24 字的一句话（保留人名、数字、约定），" +
        "只输出 JSON 数组：[{\"i\":1,\"brief\":\"…\"}]，条数和输入一一对齐。";

    // ---------------- 即时提炼（ObserveAsync 与巩固共用） ----------------

    private Task<int> ExtractFactsAsync(string subject, string transcript, CancellationToken ct)
        => ExtractFactsAsync(subject, transcript, subject == "azone" ? ExtractDiaryMax : ExtractChatMax, ct);

    /// <summary>
    /// 提炼一次事实。返回新增条数；
    /// 模型没配、调用失败、空响应返回 ModelUnavailable(-1)——调用方要把它当「这轮没跑成」，
    /// 不推进处理偏移，下轮重试，避免把没提炼过的聊天行静默标成已处理。
    /// 模型应答了但不是合法 JSON（坏响应）算「没提炼出东西」，返回 0 正常推进。
    /// </summary>
    private async Task<int> ExtractFactsAsync(string subject, string transcript, int max, CancellationToken ct)
    {
        if (transcript.Trim().Length == 0) return 0;
        var llm = _host.Get<ILlmClient>();
        if (llm is null) return ModelUnavailable;
        var system = ExtractSystem.Replace("{max}", max.ToString());

        string? raw;
        try
        {
            raw = await llm.AskAsync(new LlmOptions
            {
                Provider = _host.Str("model", "provider"),
                Model = _host.Str("model", "model"),
                System = system,
                Temperature = 0.2,
                MaxTokens = 900,
                TimeoutMs = 45_000,
            }, [new LlmMessage("user", transcript)], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消要往上抛，由 ConsolidateAsync 统一收尾
        }
        catch (Exception ex)
        {
            _log.Warn("提炼模型调用失败：", ex.Message);
            return ModelUnavailable;
        }
        if (string.IsNullOrWhiteSpace(raw)) return ModelUnavailable;
        if (MediaUtil.ExtractJsonArray(raw) is not JsonArray arr) return 0;

        var added = 0;
        foreach (var node in arr)
        {
            if (added >= max) break;
            if (node is not JsonObject f) continue;
            var text = JsonUtil.Str(f["text"], "").Trim();
            if (text.Length == 0) continue;

            var tags = new List<string>();
            if (f["tags"] is JsonArray tagNodes)
                foreach (var tag in tagNodes)
                {
                    var s = tag?.ToString() ?? "";
                    if (s.Length > 0) tags.Add(s);
                }

            var about = JsonUtil.Str(f["about"], "").Trim();
            if (about.Length == 0) about = subject;
            var importance = Math.Clamp(JsonUtil.Num(f["importance"], 0.5), 0.05, 1);
            AddInternal(text, about, DisplayNameOf(about), tags, importance, "auto", subject);
            added++;
        }
        return added;
    }

    /// <summary>chatId 的显示名（主人显示用户名，访客显示昵称）。</summary>
    private string DisplayNameOf(string subject)
    {
        if (subject.Length == 0) return "别人";
        var name = _host.Auth.DisplayNameOf(subject);
        return name.Length > 0 ? name : subject == OwnerId ? "主人" : subject;
    }

    // ---------------- 定时巩固 ----------------

    /// <summary>
    /// 一轮整理（原 memory.js :430-560）：按天消费 chatlog → 遗忘 → 建人物档案 → 总结经验 → 压缩分级。
    /// processed[day] 记录当天已消费到第几行，重跑安全。
    /// </summary>
    public async Task<JsonObject> ConsolidateAsync(bool includeToday, bool force, CancellationToken ct = default)
    {
        if (!Enabled && !force) return new JsonObject { ["ok"] = false, ["message"] = "记忆功能没开" };
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new JsonObject { ["ok"] = false, ["message"] = "已经有一轮整理在跑" };
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var throughDay = ChatLog.DayKey(includeToday ? DateTime.Now : DateTime.Now.AddDays(-1));
            var processed = Map("processed");
            var added = 0;
            var days = 0;

            foreach (var day in ChatLog.Days(_store))
            {
                ct.ThrowIfCancellationRequested();
                if (string.CompareOrdinal(day, throughDay) > 0) break;
                var lines = ChatLog.Read(_store, day).OfType<JsonObject>().ToList();
                var done = (int)JsonUtil.Num(processed[day], 0);
                if (done >= lines.Count) continue;

                // 按会话连续段分组提炼（原版按 chatId 分组后分块）。每组提炼完就推进偏移并落盘：
                // 模型挂掉只重做失败的那组，绝不把没提炼过的行标成已处理（宁可贵一点重试，不静默丢内容）
                var progressed = false;
                var gi = done;
                while (gi < lines.Count)
                {
                    var chatId = JsonUtil.Str(lines[gi]["chatId"], "");
                    var gj = gi;
                    while (gj < lines.Count
                           && string.Equals(JsonUtil.Str(lines[gj]["chatId"], ""), chatId, StringComparison.Ordinal)) gj++;

                    var isDiary = chatId == "azone";
                    var fromUser = 0;
                    for (var k = gi; k < gj; k++)
                        if (JsonUtil.Str(lines[k]["who"], "") != "bot") fromUser++;

                    if (fromUser >= (isDiary ? 1 : 3)) // 太短不值得提炼（原 :528 判定）
                    {
                        var extracted = await ExtractGroupAsync(chatId, lines, gi, gj, isDiary, ct).ConfigureAwait(false);
                        if (extracted < 0)
                        {
                            processed[day] = gi; // 只承认这组之前的进度
                            Save();
                            _log.Warn($"提炼模型不可用，{day} 停在第 {gi} 行，进度已保存，下轮重试");
                            return new JsonObject
                            {
                                ["ok"] = false,
                                ["added"] = added,
                                ["message"] = "提炼模型不可用，进度已保存，下轮重试",
                            };
                        }
                        added += extracted;
                    }
                    gi = gj;
                    processed[day] = gi;
                    progressed = true;
                    Save();
                }
                if (progressed) days++;
            }

            var forgot = ForgetPass(now);
            var profiles = await BuildProfilesAsync(now, ct).ConfigureAwait(false);
            var insights = await ReflectAsync(ct).ConfigureAwait(false) ? 1 : 0;

            lock (_gate)
            {
                var last = Map("lastConsolidate");
                last["day"] = ChatLog.DayKey(DateTime.Now);
                last["at"] = now;
                last["added"] = added;
                last["insights"] = insights;
                last["days"] = days;
                last["forgot"] = forgot;
                last["profiles"] = profiles;
                Save();
            }

            var compressed = await CompressIfNeededAsync(force, ct).ConfigureAwait(false);
            _log.Info($"整理完成：新增 {added} 条、过 {days} 天、忘 {forgot} 条、建档 {profiles} 个、压缩 {JsonUtil.Int(compressed["made"], 0)} 条");
            return new JsonObject
            {
                ["ok"] = true,
                ["added"] = added,
                ["days"] = days,
                ["forgot"] = forgot,
                ["profiles"] = profiles,
                ["insights"] = insights,
                ["compressed"] = compressed,
            };
        }
        catch (OperationCanceledException)
        {
            return new JsonObject { ["ok"] = false, ["message"] = "整理被取消" };
        }
        catch (Exception ex)
        {
            // 任何意外都不外抛（Tick 的定时器不该被炸掉）；偏移已按组落盘，下轮从断点继续
            _log.Warn("整理失败（进度已按组保存，下轮重试）：", ex.Message);
            return new JsonObject { ["ok"] = false, ["message"] = ex.Message };
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 提炼一个会话连续段（80 行一块）。返回新增条数；模型不可用返回 ModelUnavailable(-1)，
    /// 调用方不得推进偏移。组内中途失败时，前面块提炼出的条目已入库，下轮会重提炼整组（有相似度去重兜底）。
    /// </summary>
    private async Task<int> ExtractGroupAsync(string chatId, List<JsonObject> lines, int from, int to, bool isDiary, CancellationToken ct)
    {
        var added = 0;
        for (var i = from; i < to; i += ChunkLines)
        {
            var piece = lines.GetRange(i, Math.Min(ChunkLines, to - i));
            var r = await ExtractDayChunkAsync(chatId, piece, isDiary, ct).ConfigureAwait(false);
            if (r < 0) return ModelUnavailable;
            added += r;
        }
        return added;
    }

    /// <summary>把一段 chatlog 行拼成「说话人：内容」的文稿再提炼。返回新增条数；模型不可用返回 ModelUnavailable(-1)。</summary>
    private async Task<int> ExtractDayChunkAsync(string chatId, List<JsonObject> piece, bool isDiary, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var line in piece)
        {
            var who = JsonUtil.Str(line["who"], "");
            var text = JsonUtil.Str(line["text"], "").Trim();
            if (text.Length == 0) continue;
            if (who == "bot")
            {
                sb.Append("我：");
            }
            else
            {
                var uid = JsonUtil.Str(line["uid"], "");
                sb.Append(DisplayNameOf(uid.Length > 0 ? uid : chatId)).Append('：');
            }
            sb.AppendLine(text);
        }
        if (sb.Length == 0) return 0;
        var max = isDiary ? ExtractDiaryMax : ExtractChatMax;
        return await ExtractFactsAsync(chatId, sb.ToString(), max, ct).ConfigureAwait(false);
    }

    // ---------------- 遗忘 ----------------

    /// <summary>原 memory.js :563-585：过期/低价值记忆清掉，返回清了几条。</summary>
    private int ForgetPass(long now)
    {
        List<JsonObject> forget;
        lock (_gate)
        {
            var items = Items();
            forget = items.OfType<JsonObject>().Where(m => ShouldForget(m, now)).ToList();
            foreach (var m in forget) items.Remove(m);
            if (forget.Count > 0)
            {
                BumpRev();
                Save();
            }
        }
        return forget.Count;
    }

    private static bool ShouldForget(JsonObject m, long now)
    {
        var invalidAt = (long)JsonUtil.Num(m["invalidAt"], 0);
        if (invalidAt > 0) return now - invalidAt > 180 * DayMs; // 被替代超 180 天 → 忘（原 :565）
        if (MemoryText.Source(m) != "auto") return false;        // 手动/经历不自动忘（原 :567）
        if (JsonUtil.Num(m["importance"], 0.5) >= 0.6) return false; // 重要度 ≥0.6 不忘（原 :568）
        return Retention(m, now) < 0.05;                          // 记忆强度不足（原 :569）
    }

    /// <summary>retention = exp(-days / tau)，tau = 20 * strength * (0.5 + importance)（原 :571-573）。</summary>
    private static double Retention(JsonObject m, long now)
    {
        var last = Math.Max(MemoryText.At(m), MemoryText.LastHit(m));
        var days = Math.Max(0, (now - last) / (double)DayMs);
        var strength = 1 + Math.Min(10, MemoryText.HitDays(m));
        var tau = 20.0 * strength * (0.5 + JsonUtil.Num(m["importance"], 0.5));
        return Math.Exp(-days / Math.Max(1, tau));
    }

    // ---------------- 人物档案 ----------------

    /// <summary>原 memory.js :590-640：同一个 about 攒够 3 条且档案 3 天没更新 → 重建档案。</summary>
    private async Task<int> BuildProfilesAsync(long now, CancellationToken ct)
    {
        var groups = Items().OfType<JsonObject>()
            .Where(m => !MemoryText.IsInvalid(m))
            .Where(m => MemoryText.About(m).Length > 0)
            .GroupBy(MemoryText.About, StringComparer.Ordinal)
            .Where(g => g.Count() >= ProfileMinItems)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .ToList();

        var made = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var about = group.Key;
            if (ProfileOf(about) is JsonObject existing
                && now - (long)JsonUtil.Num(existing["at"], 0) < 3 * DayMs) continue;

            var lines = group
                .OrderByDescending(MemoryText.At)
                .Take(12)
                .Select(m => "- " + MemoryText.Text(m))
                .ToList();
            var user = $"关于「{DisplayNameOf(about)}」的记忆：\n" + string.Join("\n", lines);
            var raw = await MediaUtil.AskModelAsync(_host, ProfileSystem, user, 0.3, 500, 45_000, ct).ConfigureAwait(false);
            var obj = MediaUtil.ExtractJsonObject(raw ?? "");
            if (obj is null) continue;
            var text = JsonUtil.Str(obj["text"], "").Trim();
            if (text.Length == 0) continue;
            var name = JsonUtil.Str(obj["name"], "").Trim();
            SetProfile(about, text, name);
            made++;
        }
        return made;
    }

    // ---------------- 总结经验 + 说话方式 ----------------

    /// <summary>原 memory.js :642-690：从最近记忆总结 insights（和主人相处的经验）+ self style。</summary>
    private async Task<bool> ReflectAsync(CancellationToken ct)
    {
        var user = string.Join("\n", Items().OfType<JsonObject>()
            .Where(m => !MemoryText.IsInvalid(m))
            .OrderByDescending(MemoryText.At)
            .Take(40)
            .Select(m => "- " + MemoryText.Text(m)));
        if (user.Length == 0) return false;

        var raw = await MediaUtil.AskModelAsync(_host, ReflectSystem, user, 0.4, 600, 45_000, ct).ConfigureAwait(false);
        var obj = MediaUtil.ExtractJsonObject(raw ?? "");
        if (obj is null) return false;

        var insights = ReadLines(obj["insights"] as JsonArray);
        var style = ReadLines(obj["style"] as JsonArray);
        if (insights.Count > 0) SetInsights(OwnerId, ToJsonArray(insights), DisplayNameOf(OwnerId));
        if (style.Count > 0) SetStyle("self", ToJsonArray(style));
        return insights.Count > 0 || style.Count > 0;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> list)
    {
        var arr = new JsonArray();
        foreach (var s in list) arr.Add(s);
        return arr;
    }

    // ---------------- 压缩分级 ----------------

    /// <summary>
    /// 原 memory.js :695-760：compressEveryHours 到点 → 拿最老的 40 条未分级记忆压一轮。
    /// 重要度 ≥0.7 保持全文、≥0.3 缩成 brief、≥0.05 只留 kw、其余 drop（召回时不再出现）。
    /// </summary>
    private async Task<JsonObject> CompressIfNeededAsync(bool force, CancellationToken ct)
    {
        // Tick 和整理收尾都可能调到这里：同时只跑一轮，防两轮拿同一批未分级条目互相覆盖
        if (Interlocked.CompareExchange(ref _compressBusy, 1, 0) != 0)
            return new JsonObject { ["ok"] = true, ["skipped"] = true, ["message"] = "已有一轮压缩在跑" };
        try
        {
            return await CompressIfNeededLockedBodyAsync(force, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _compressBusy, 0);
        }
    }

    private async Task<JsonObject> CompressIfNeededLockedBodyAsync(bool force, CancellationToken ct)
    {
        var everyHours = Math.Max(1, _host.Int(6, "memory", "compressEveryHours"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = Load()["lastCompress"] as JsonObject;
        var lastAt = (long)JsonUtil.Num(last?["at"], 0);
        if (!force && lastAt > 0 && now - lastAt < everyHours * 3_600_000L)
            return new JsonObject { ["ok"] = true, ["made"] = 0, ["skipped"] = true };

        var batch = Items().OfType<JsonObject>()
            .Where(m => !MemoryText.IsInvalid(m))
            .Where(m => JsonUtil.Str(m["grade"], "").Length == 0)
            .Where(m => !(MemoryText.Source(m) == "event" && now - MemoryText.At(m) < DayMs)) // 24h 内经历不压（原 :703）
            .OrderBy(MemoryText.At)                                // 最旧的先压（原 :715 口径），新记忆等它老一老
            .ThenBy(m => MemoryText.Id(m), StringComparer.Ordinal) // 同毫秒按 id 稳定排序，批次可复现
            .Take(CompressBatch)
            .ToList();

        if (batch.Count == 0)
        {
            RecordCompress(now, 0, 0, 0, 0);
            return new JsonObject { ["ok"] = true, ["made"] = 0 };
        }

        var listSb = new StringBuilder();
        for (var i = 0; i < batch.Count; i++)
            listSb.AppendLine($"{i + 1}. {MemoryText.Text(batch[i])}");
        var raw = await MediaUtil.AskModelAsync(_host, CompressSystem, listSb.ToString(), 0.2,
            batch.Count * 40 + 200, 45_000, ct).ConfigureAwait(false);

        var briefs = new Dictionary<int, string>();
        if (MediaUtil.ExtractJsonArray(raw ?? "") is JsonArray arr)
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var idx = JsonUtil.Int(o["i"], 0) - 1;
                var line = JsonUtil.Str(o["brief"], "").Trim();
                if (idx >= 0 && idx < batch.Count && line.Length > 0) briefs[idx] = line;
            }

        // 模型空/坏响应：一条可用缩写都没拿到就不动任何条目。分级一旦写上就不会再重压，
        // 宁可这轮跳过（记 failed 退避，下个窗口或 force 重试），也不能把整批记忆静默降级成机械截断。
        if (briefs.Count == 0)
        {
            lock (_gate)
            {
                var rec = Map("lastCompress");
                rec["at"] = now; // 记失败时间：按 compressEveryHours 退避，不让 Tick 每分钟轰炸模型
                rec["failed"] = true;
                rec["reason"] = string.IsNullOrWhiteSpace(raw) ? "模型空响应" : "模型响应无法解析";
                rec["made"] = 0;
                Save();
            }
            _log.Warn("压缩分级跳过：模型没给出可用缩写（记忆条目未动，下轮重试）");
            return new JsonObject { ["ok"] = false, ["skipped"] = true, ["message"] = "压缩模型无可用响应" };
        }

        var gradeFull = _host.Num(0.7, "memory", "gradeFull");
        var gradeBrief = _host.Num(0.3, "memory", "gradeBrief");
        var gradeDrop = _host.Num(0.05, "memory", "gradeDrop");
        int full = 0, brief = 0, kw = 0, drop = 0;

        lock (_gate)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var m = batch[i];
                var imp = JsonUtil.Num(m["importance"], 0.5);
                if (imp >= gradeFull)
                {
                    m["grade"] = "full";
                    full++;
                }
                else if (imp >= gradeBrief)
                {
                    m["grade"] = "brief";
                    m["brief"] = briefs.GetValueOrDefault(i, Truncate(MemoryText.Text(m), 24));
                    brief++;
                }
                else if (imp >= gradeDrop)
                {
                    m["grade"] = "kw";
                    m["kw"] = briefs.GetValueOrDefault(i, Truncate(MemoryText.Text(m), 16));
                    kw++;
                }
                else
                {
                    m["grade"] = "drop";
                    drop++;
                }
            }
            BumpRev();
            RecordCompress(now, full, brief, kw, drop);
            Save();
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["made"] = brief + kw + drop,
            ["full"] = full,
            ["brief"] = brief,
            ["kw"] = kw,
            ["drop"] = drop,
        };
    }

    private void RecordCompress(long now, int full, int brief, int kw, int drop)
    {
        var rec = Map("lastCompress");
        rec["at"] = now;
        rec.Remove("failed"); // 成功（或确认没活干）就清掉失败标记
        rec.Remove("reason");
        rec["full"] = full;
        rec["brief"] = brief;
        rec["kw"] = kw;
        rec["drop"] = drop;
        rec["made"] = brief + kw + drop;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    // ---------------- 定时入口（LifeModule 每分钟调） ----------------

    /// <summary>consolidateTimes 到点跑一轮整理；compressEveryHours 到点压一轮。</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!Enabled) return;
        var now = DateTime.Now;
        var hhmm = now.ToString("HH:mm");
        if (_host.Node("memory", "consolidateTimes") is JsonArray times)
        {
            foreach (var slot in times)
            {
                var s = slot?.ToString() ?? "";
                if (s != hhmm) continue;
                var runs = Map("runs");
                if (JsonUtil.Str(runs[hhmm], "") != ChatLog.DayKey(now))
                {
                    runs[hhmm] = ChatLog.DayKey(now); // 同一天同一时段只跑一次（原 :434）
                    Save();
                    // 04:00 这类凌晨时段只整理到昨天；中午及以后的时段连今天上午一起整理
                    var includeToday = TimeToMinutes(s) >= 720;
                    var r = await ConsolidateAsync(includeToday, false, ct).ConfigureAwait(false);
                    // 整理没跑成（模型挂了/被取消/撞车）就摘掉标记，下个 tick 重试；
                    // 处理偏移是按组落盘的，重试从断点继续，不会重复提炼
                    if (!((bool?)r["ok"] ?? false))
                    {
                        lock (_gate)
                        {
                            var rr = Map("runs");
                            if (JsonUtil.Str(rr[hhmm], "") == ChatLog.DayKey(now)) rr.Remove(hhmm);
                            Save();
                        }
                    }
                }
                break;
            }
        }
        await CompressIfNeededAsync(false, ct).ConfigureAwait(false);
    }

    private static int TimeToMinutes(string s)
    {
        var parts = s.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            ? h * 60 + m
            : 0;
    }

    // ---------- 工具面（remember 带 about 的写入 + 管理面清召回日志） ----------

    /// <summary>remember 工具用：带 about 的一条「自动」记忆。</summary>
    public JsonObject AddAbout(string text, double importance, string? about, string? aboutName, IEnumerable<string>? tags)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return new JsonObject { ["ok"] = false };
        return AddInternal(t, (about ?? "").Trim(), (aboutName ?? "").Trim(), tags?.ToArray() ?? [],
            Math.Clamp(importance, 0, 1), "tool", "");
    }

    /// <summary>清空召回日志（测试按钮用）。内存和落盘一起清。</summary>
    public void ClearRecalls()
    {
        lock (_gate)
        {
            _recallLog.Clear();
            PersistRecallLogLocked();
            Save();
        }
    }
}
