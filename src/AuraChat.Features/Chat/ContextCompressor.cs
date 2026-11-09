using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Chat;

/// <summary>
/// 上下文压缩（2026-11-04 拍板）：聊天记录原文永不删；压缩只生成"摘要视图"。
/// 每 6 小时跑一次（每天 4 次）：把最近窗口之外的低权重文本用模型压成摘要，
/// 摘要 + 被压缩范围（coversFrom/coversTo 毫秒、coversToId/coversToPos 边界锚点、条数）持久化在 compress.json。
/// 单轮最多 MaxPerRun 条；一轮装不下的积压分成多轮压，每轮一段 part 追加进 chats.&lt;chatId&gt;.parts，
/// 平铺字段（summary/coversFrom/coversTo/coversCount）始终是全部 parts 合并后的视图，边界单调不回退。
/// 图片从不进文本上下文（只进当轮多模态），无需压缩。
/// </summary>
public sealed class ContextCompressor
{
    /// <summary>每天压缩 4 次 = 每 6 小时。</summary>
    public const int IntervalMinutes = 360;

    /// <summary>单次压缩最多带多少条原文给模型（防 token 爆炸）。</summary>
    public const int MaxPerRun = 40;

    /// <summary>上下文里最近 N 条原文不压（保留新鲜感）。</summary>
    public const int KeepRecent = 30;

    /// <summary>一个会话最多留几个摘要分段（再老的分段被挤掉时，覆盖范围同步收缩，不虚报）。</summary>
    public const int MaxSummaryParts = 8;

    /// <summary>单段摘要最长字符数（防模型跑飞把上下文撑爆）。</summary>
    public const int SummaryMaxChars = 2000;

    /// <summary>翻历史用的页大小。</summary>
    private const int PageSize = 100;

    private readonly FeatureHost _host;
    private readonly IHistoryStore _history;
    private readonly AreaLog _log;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    public ContextCompressor(FeatureHost host, IHistoryStore history)
    {
        _host = host;
        _history = history;
        _log = host.Log.Child("compress");
        _file = host.Store.PathIn("compress.json");
    }

    private bool Enabled => _host.Flag(true, "context", "compress");

    public void Start()
    {
        if (_timer is not null) return;
        // 启动 2 分钟后先试一次（有积压就压），然后每 6 小时一轮
        _timer = new Timer(async _ =>
        {
            try { await RunOnceAsync(default); }
            catch (Exception ex) { _log.Warn("压缩任务异常：", ex.Message); }
        }, null, 2 * 60_000, IntervalMinutes * 60_000);
    }

    public void Stop() => _timer?.Dispose();

    // ---------------- 摘要存取 ----------------

    private JsonObject Load()
    {
        var root = DataStore.ReadObject(_file);
        if (root["chats"] is JsonObject) return root;
        return new JsonObject { ["chats"] = new JsonObject(), ["updatedAt"] = 0 };
    }

    private void Save(JsonObject root)
    {
        root["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        DataStore.WriteJson(_file, root);
    }

    /// <summary>取某会话的压缩摘要（ChatHub 拼上下文用）：合并后的摘要文本 + 覆盖范围。</summary>
    public JsonObject? SummaryOf(string chatId)
    {
        var root = Load();
        return root["chats"] is JsonObject chats && chats[chatId] is JsonObject s ? s : null;
    }

    /// <summary>
    /// 上次压缩覆盖到哪。平铺字段就是合并视图：
    /// coversTo/coversToId（最后一条被压消息的时间戳与 id）+ coversToPos（它在整条历史里的 1 基序位）。
    /// coversToPos == 0 表示没有可用的位置锚点（没压过，或旧版本写的数据）。
    /// </summary>
    private (long To, string ToId, int ToPos) CoverageOf(string chatId)
    {
        var s = SummaryOf(chatId);
        if (s is null) return (0, "", 0);
        return (LongT(s["coversTo"]), s["coversToId"]?.ToString() ?? "", (int)LongT(s["coversToPos"]));
    }

    // ---------------- 压缩一轮 ----------------

    /// <summary>跑一次压缩。返回 {ok, summarized, skipped, message}。</summary>
    public async Task<JsonObject> RunOnceAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return new JsonObject { ["ok"] = false, ["skipped"] = true, ["message"] = "压缩开关关着" };

        var llm = _host.Get<ILlmClient>();
        if (llm is null)
            return new JsonObject { ["ok"] = false, ["skipped"] = true, ["message"] = "模型没配好" };

        var chatId = AuraChatInfo.OwnerId;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 取"最旧未压缩、且不在保留窗口内"的最多 MaxPerRun 条
            var (batch, endPos) = TakeUncompressedBatch(chatId, KeepRecent, MaxPerRun);
            if (batch.Count == 0)
                return new JsonObject { ["ok"] = true, ["summarized"] = 0, ["message"] = "没有要压的积压" };

            var prompt = BuildBatchText(batch);
            var system = "你是记忆整理助手。把这段 AI 伴侣与主人的聊天记录压成一份摘要，"
                + "保留：重要约定、对方偏好、情绪转折、悬而未决的事、提到的物品/人名/时间。"
                + "口语原话不必保留，第三人称「对方/她」叙述。300 字以内，直接输出摘要正文。";

            string? summary;
            try
            {
                summary = await llm.AskAsync(new LlmOptions
                {
                    Provider = _host.Str("model", "provider"),
                    Model = _host.Str("model", "model"),
                    System = system,
                    Temperature = 0.2,
                    MaxTokens = 500,
                    TimeoutMs = 60_000,
                }, [new LlmMessage("user", prompt)], ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("压缩模型调用失败，这轮跳过（原文没动）：", ex.Message);
                return new JsonObject { ["ok"] = false, ["skipped"] = true, ["message"] = ex.Message };
            }

            // 模型空/坏响应：什么都不持久化，下轮重试同一批（原文没动）
            if (string.IsNullOrWhiteSpace(summary))
            {
                _log.Warn("压缩模型返回空，这轮跳过（原文没动）");
                return new JsonObject { ["ok"] = false, ["skipped"] = true, ["message"] = "模型返回空" };
            }
            var text = summary.Trim();
            if (text.Length > SummaryMaxChars) text = text[..SummaryMaxChars];

            PersistSummary(chatId, batch, endPos, text);
            _log.Info($"压缩完成：{batch.Count} 条 → 摘要 {text.Length} 字（原文不动）");
            return new JsonObject { ["ok"] = true, ["summarized"] = batch.Count };
        }
        catch (Exception ex)
        {
            _log.Warn("压缩失败（原文没动，下轮重试）：", ex.Message);
            return new JsonObject { ["ok"] = false, ["message"] = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把一轮摘要追加进会话槽位：parts 按时间顺序累积（老段在前），平铺字段刷新成合并视图。
    /// 同一 chatId 的覆盖边界只前进不回退；旧版本写的平铺格式在首次写入时并入 parts[0]，不丢内容。
    /// </summary>
    private void PersistSummary(string chatId, List<JsonObject> batch, int endPos, string text)
    {
        var root = Load();
        var chats = (JsonObject)root["chats"]!;
        var slot = chats[chatId] as JsonObject ?? new JsonObject();

        // 覆盖序位只前进不回退：算出比现有锚点更早的批次说明中途出了岔子，
        // 宁可浪费这轮模型调用也不把覆盖声明写回去（写回去会造成已压区被重复/漏压）
        var existingPos = (int)LongT(slot["coversToPos"]);
        if (existingPos >= endPos)
        {
            _log.Warn($"压缩覆盖序位未前进（{existingPos} → {endPos}），丢弃本轮结果");
            return;
        }

        var parts = slot["parts"] as JsonArray;
        if (parts is null)
        {
            parts = new JsonArray();
            // 旧格式迁移：只有平铺摘要、没有 parts 时，先把旧摘要并成第一段（它的覆盖在本批之前，顺序正确）
            if (slot["summary"] is { } legacy && legacy.ToString().Length > 0)
            {
                parts.Add(new JsonObject
                {
                    ["summary"] = legacy.ToString(),
                    ["coversFrom"] = slot["coversFrom"]?.DeepClone() ?? 0,
                    ["coversTo"] = slot["coversTo"]?.DeepClone() ?? 0,
                    ["coversCount"] = slot["coversCount"]?.DeepClone() ?? 0,
                    ["at"] = slot["at"]?.DeepClone() ?? 0,
                });
            }
            slot["parts"] = parts; // 新建的要挂回槽位，否则这轮摘要写了个寂寞
        }

        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        parts.Add(new JsonObject
        {
            ["summary"] = text,
            ["coversFrom"] = LongT(batch[0]["t"]),
            ["coversTo"] = LongT(batch[^1]["t"]),
            ["coversToId"] = batch[^1]["id"]?.ToString() ?? "",
            ["coversToPos"] = endPos,
            ["coversCount"] = batch.Count,
            ["at"] = at,
        });
        // 段数超限：挤掉最老的段，覆盖声明的起点跟着收缩（不虚报还覆盖着已丢弃的区间）
        while (parts.Count > MaxSummaryParts) parts.RemoveAt(0);

        var total = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var node in parts)
        {
            if (node is not JsonObject p) continue;
            total += (int)LongT(p["coversCount"]);
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p["summary"]?.ToString() ?? "");
        }

        var last = parts[^1] as JsonObject;
        slot["summary"] = sb.ToString();
        slot["coversFrom"] = parts[0] is JsonObject first ? LongT(first["coversFrom"]) : 0;
        slot["coversTo"] = last is null ? 0 : LongT(last["coversTo"]);
        slot["coversToId"] = last?["coversToId"]?.ToString() ?? "";
        slot["coversToPos"] = last is null ? 0 : (int)LongT(last["coversToPos"]);
        slot["coversCount"] = total;
        slot["at"] = at;
        chats[chatId] = slot;
        Save(root);
    }

    // ---------------- 批次选择 ----------------

    /// <summary>
    /// 取「最旧的、未落进上次摘要覆盖范围、且不在最近保留窗口」的原文批次，返回（批次, 批内最后一条的 1 基序位）。
    /// 序位锚点（coversToPos）是权威边界：上次压到第几条，这次就从第几条之后接着压，
    /// 同一毫秒的多条消息不会被时间戳边界吞掉；翻页游标会在同毫秒边界重复吐消息，按 id 去重。
    /// </summary>
    private (List<JsonObject> Batch, int EndPos) TakeUncompressedBatch(string chatId, int keepRecent, int maxPerRun)
    {
        var (coversTo, coversToId, coversToPos) = CoverageOf(chatId);
        var total = _history.TotalOf(chatId);
        var regionCount = total - keepRecent; // 最旧的 regionCount 条属于"可压区"
        if (regionCount <= 0) return ([], 0);

        if (coversToPos > 0)
            return TakeAfterAnchor(chatId, total, regionCount, coversTo, coversToId, coversToPos, maxPerRun);
        return TakeFromOldest(chatId, regionCount, coversTo, coversToId, maxPerRun);
    }

    /// <summary>
    /// 有序位锚点时的快速路径：批次永远紧跟锚点之后（最旧未压优先），从新往旧翻页、
    /// 数着「离最新还有几条」跳过保留窗口，正好取到锚点后面的 maxPerRun 条，不用翻完整库。
    /// </summary>
    private (List<JsonObject> Batch, int EndPos) TakeAfterAnchor(string chatId, long total, int regionCount,
        long coversTo, string coversToId, int coversToPos, int maxPerRun)
    {
        var uncovered = regionCount - coversToPos; // 锚点之后还剩多少条可压（非文本的占位也算）
        if (uncovered <= 0) return ([], 0);
        var take = Math.Min(maxPerRun, uncovered);
        var skipNewest = (int)total - coversToPos - take; // 批次之后还压不动的（保留窗口 + 更晚的未压批次）

        // 收的时候按「批内序」记下每条的绝对序位；批次窗口可能横跨多页，
        // 页与页是按新→旧走的，最后要按序位排回时间正序，endPos 取真正压掉的最大序位
        var picked = new List<(int Pos, JsonObject Item)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var freshBefore = 0; // 本页之前已见过的消息数（= 比本页更新的那些）
        long cursor = 0;
        while (true)
        {
            var (page, oldestAt, hasMore) = _history.Page(chatId, cursor, PageSize);
            if (page.Count == 0) break;
            var pageFresh = new List<JsonObject>();
            foreach (var node in page)
            {
                if (node is not JsonObject msg) continue;
                if (seen.Add(DedupeKey(msg))) pageFresh.Add(msg); // 同毫秒翻页边界会重复吐这条
            }
            // 页内旧→新排列：idx 越大越新，rank（离最新第几条，1 基）越小
            for (var idx = 0; idx < pageFresh.Count; idx++)
            {
                var msg = pageFresh[idx];
                var rank = freshBefore + pageFresh.Count - idx;
                if (rank <= skipNewest) break;           // 进保留窗口了：批次窗口走完
                if (rank > skipNewest + take) continue;  // 比批次更旧的（已覆盖区，深挖多页时才出现）
                if (coversToId.Length > 0 && coversToId.Equals(msg["id"]?.ToString() ?? "", StringComparison.Ordinal))
                {
                    // 锚点居然落在批次窗口里：序位锚点失准（多半是走页期间有新消息插入），退回全量慢路径重算
                    _log.Warn("压缩序位锚点与消息 id 不符，退回全量扫描");
                    return TakeFromOldest(chatId, regionCount, coversTo, coversToId, maxPerRun);
                }
                var positionInSlice = skipNewest + take + 1 - rank; // 批内序（1 = 批内最旧）
                if (IsSummarizable(msg))
                    picked.Add((coversToPos + positionInSlice, (JsonObject)msg.DeepClone()));
            }
            freshBefore += pageFresh.Count;
            if (freshBefore >= skipNewest + take) break; // 批次窗口走完（哪怕一条没收到）
            if (!hasMore || oldestAt <= 0 || pageFresh.Count == 0) break;
            cursor = oldestAt;
        }
        if (picked.Count == 0) return ([], 0);
        var batch = picked.OrderBy(p => p.Pos).Select(p => p.Item).ToList();
        return (batch, picked.Max(p => p.Pos));
    }

    /// <summary>
    /// 没有序位锚点（从未压过，或旧版本数据）时的全量路径：翻完整库、按时间正序排好，
    /// 从可压区最旧一头开始，跳过已覆盖边界（有 id 按 id 对齐，老数据退回时间戳），收最多 maxPerRun 条。
    /// 只在有覆盖缺口时走，跑完一轮就有了锚点，之后都走快速路径。
    /// </summary>
    private (List<JsonObject> Batch, int EndPos) TakeFromOldest(string chatId, int regionCount,
        long coversTo, string coversToId, int maxPerRun)
    {
        // 翻页是新→旧走的；先整库去重收下来，再把页序倒回来就得到时间正序
        var pages = new List<List<JsonObject>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long cursor = 0;
        while (true)
        {
            var (page, oldestAt, hasMore) = _history.Page(chatId, cursor, PageSize);
            var fresh = new List<JsonObject>();
            foreach (var node in page)
            {
                if (node is not JsonObject msg) continue;
                if (seen.Add(DedupeKey(msg))) fresh.Add(msg);
            }
            if (fresh.Count > 0) pages.Add(fresh);
            if (!hasMore || oldestAt <= 0 || fresh.Count == 0) break;
            cursor = oldestAt;
        }

        pages.Reverse(); // 现在是最旧的页在前，页内本来就是旧→新
        var collected = new List<JsonObject>();
        var boundaryPassed = coversTo <= 0 && coversToId.Length == 0;
        var endPos = 0;
        var position = 0;
        foreach (var page in pages)
        {
            foreach (var msg in page)
            {
                position++;
                if (position > regionCount) break; // 进保留窗口了
                var t = LongT(msg["t"]);
                var id = msg["id"]?.ToString() ?? "";
                if (!boundaryPassed)
                {
                    if (coversToId.Length > 0 && id.Length > 0)
                    {
                        // 按 id 精确对齐：边界这条本身已压过，同毫秒的后续消息照样能收
                        if (!coversToId.Equals(id, StringComparison.Ordinal)) continue;
                        boundaryPassed = true;
                        continue;
                    }
                    if (t <= coversTo) continue; // 老数据没有 id：退回时间戳边界（同毫秒未压的会漏，属已知旧格式局限）
                    boundaryPassed = true;
                }
                if (IsSummarizable(msg))
                {
                    collected.Add((JsonObject)msg.DeepClone());
                    endPos = position;
                    if (collected.Count >= maxPerRun) return (collected, endPos);
                }
            }
            if (position > regionCount) break;
        }
        return (collected, endPos);
    }

    /// <summary>值得进文本摘要的消息：纯文本，或带说明文字的其他类型（图片说明等）。</summary>
    private static bool IsSummarizable(JsonObject msg)
        => msg["kind"]?.ToString() is null or "text" || !string.IsNullOrEmpty(Text(msg["text"]));

    /// <summary>翻页去重键：有 id 用 id；老数据没 id 就按内容近似（同毫秒同内容视为同一条）。</summary>
    private static string DedupeKey(JsonObject msg)
    {
        var id = msg["id"]?.ToString() ?? "";
        if (id.Length > 0) return "i:" + id;
        return "t:" + LongT(msg["t"]) + ":" + Text(msg["who"]) + ":" + Text(msg["text"]);
    }

    private static long LongT(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<long>(); } catch { /* 不是 long 再试 */ }
        try { return (long)node.GetValue<double>(); } catch { /* 再试字符串 */ }
        return long.TryParse(node.ToString(), out var n) ? n : 0;
    }

    private static string Text(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<string>(); } catch { return node.ToString(); }
    }

    private static string BuildBatchText(List<JsonObject> batch)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in batch)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(LongT(m["t"]))
                .ToLocalTime().ToString("MM-dd HH:mm");
            var who = Text(m["who"]) == "bot" ? "她" : "对方";
            sb.AppendLine($"{time} {who}: {Text(m["text"])}");
        }
        return sb.ToString();
    }
}
