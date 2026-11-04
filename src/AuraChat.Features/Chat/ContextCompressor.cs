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
/// 摘要 + 被压缩范围（chatId/from/to/条数）持久化在 compress.json。
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

    /// <summary>取某会话的压缩摘要（ChatHub 拼上下文用）：最近一次摘要文本 + 覆盖范围。</summary>
    public JsonObject? SummaryOf(string chatId)
    {
        var root = Load();
        return root["chats"] is JsonObject chats && chats[chatId] is JsonObject s ? s : null;
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
        var total = _history.TotalOf(chatId);
        var keep = KeepRecent;
        // 窗口外还有多少条没被摘要覆盖 → 有积压才压
        var coveredUntil = CoveredUntil(chatId);
        var pending = CoveredUntilIsNewer(chatId) ? CountBetween(chatId, coveredUntil, take: int.MaxValue, excludeLast: keep) : 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 取"最旧未压缩、且不在保留窗口内"的最多 MaxPerRun 条
            var batch = TakeUncompressedBatch(chatId, keep, MaxPerRun);
            if (batch.Count == 0)
                return new JsonObject { ["ok"] = true, ["summarized"] = 0, ["message"] = "没有要压的积压" };

            var prompt = BuildBatchText(batch);
            var system = "你是记忆整理助手。把这段 AI 伴侣与主人的聊天记录压成一份摘要，"
                + "保留：重要约定、对方偏好、情绪转折、悬而未决的事、提到的物品/人名/时间。"
                + "口语原话不必保留，第三人称「对方/她」叙述。300 字以内，直接输出摘要正文。";

            var summary = await llm.AskAsync(new LlmOptions
            {
                Provider = _host.Str("model", "provider"),
                Model = _host.Str("model", "model"),
                System = system,
                Temperature = 0.2,
                MaxTokens = 500,
                TimeoutMs = 60_000,
            }, [new LlmMessage("user", prompt)], ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(summary))
            {
                _log.Warn("压缩模型返回空，这轮跳过（原文没动）");
                return new JsonObject { ["ok"] = false, ["skipped"] = true, ["message"] = "模型返回空" };
            }

            // 持久化：摘要 + 覆盖范围（from/to 毫秒 + 条数）。原文永远不动。
            var root = Load();
            var chats = (JsonObject)root["chats"]!;
            chats[chatId] = new JsonObject
            {
                ["summary"] = summary.Trim(),
                ["coversFrom"] = batch[0]["t"]!.DeepClone(),
                ["coversTo"] = batch[^1]["t"]!.DeepClone(),
                ["coversCount"] = batch.Count,
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Save(root);
            _log.Info($"压缩完成：{batch.Count} 条 → 摘要 {summary.Trim().Length} 字（原文不动）");
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

    /// <summary>取"最旧的、未落进上次摘要覆盖范围、且不在最近保留窗口"的原文批次。</summary>
    private List<JsonObject> TakeUncompressedBatch(string chatId, int keepRecent, int maxPerRun)
    {
        var coversTo = CoveredUntil(chatId);
        // 从最旧往新翻，收 coversTo 之后、且不属于最后 keepRecent 条的
        var total = _history.TotalOf(chatId);
        var skipFromEnd = keepRecent;
        var collected = new List<JsonObject>();
        long cursor = 0;
        var remainingToSkip = total - skipFromEnd; // 最旧的 (total - keepRecent) 条属于"可压区"

        while (collected.Count < maxPerRun)
        {
            var (page, oldestAt, hasMore) = _history.Page(chatId, cursor, 100);
            if (page.Count == 0) break;
            foreach (var m in page)
            {
                if (m is not JsonObject msg) continue;
                if (remainingToSkip <= 0) break; // 进保留窗口了，停
                var t = msg["t"]?.GetValue<long>() ?? 0;
                if (t > coversTo && (msg["kind"]?.ToString() is null or "text" || !string.IsNullOrEmpty(msg["text"]?.ToString())))
                    collected.Add((JsonObject)msg.DeepClone());
                remainingToSkip--;
                if (collected.Count >= maxPerRun) break;
            }
            if (remainingToSkip <= 0 || !hasMore || oldestAt <= 0) break;
            cursor = oldestAt;
        }
        return collected;
    }

    private long CoveredUntil(string chatId)
    {
        var s = SummaryOf(chatId);
        long v = 0;
        if (s?["coversTo"] is { } node)
            _ = long.TryParse(node.ToString(), out v);
        return v;
    }

    private bool CoveredUntilIsNewer(string chatId) => CoveredUntil(chatId) > 0;

    private int CountBetween(string chatId, long fromTs, int take, int excludeLast) => 0; // 仅用于日志占位

    private static string BuildBatchText(List<JsonObject> batch)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in batch)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(m["t"]?.GetValue<long>() ?? 0)
                .ToLocalTime().ToString("MM-dd HH:mm");
            var who = m["who"]?.ToString() == "bot" ? "她" : "对方";
            sb.AppendLine($"{time} {who}: {m["text"]}");
        }
        return sb.ToString();
    }
}
