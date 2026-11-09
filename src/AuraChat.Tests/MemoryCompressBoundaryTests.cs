using System.Text.Json.Nodes;
using AuraChat.Core.Ai;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Chat;
using AuraChat.Features.Life;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// 按脚本回话的假模型：给 MemoryService / ContextCompressor 的边界与故障注入测试用。
/// 脚本队列里放 string（原样返回）、null（空响应）或 Exception（抛出）；
/// 队列空了直接抛错，避免测试在没喂够脚本时悄悄走了别的分支。
/// </summary>
internal sealed class ScriptedLlm : ILlmClient
{
    public Queue<object?> Script { get; } = new();

    /// <summary>AskAsync 收到的 user 文本（按次序）。</summary>
    public List<string> Prompts { get; } = [];

    public Task<string> AskAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        Prompts.Add(messages.Count > 0 ? messages[^1].Content : "");
        if (Script.Count == 0) throw new InvalidOperationException("模型脚本空了：测试没喂够应答");
        var next = Script.Dequeue();
        if (next is Exception ex) throw ex;
        return Task.FromResult((string?)next ?? "");
    }

    public async Task<JsonObject> AskMessageAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages,
        JsonArray? tools = null, JsonArray? toolHistory = null, CancellationToken ct = default)
    {
        var text = await AskAsync(options, messages, ct);
        return new JsonObject { ["content"] = text };
    }

    public IAsyncEnumerable<string> StreamAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
        => AsyncEnum();

    private async IAsyncEnumerable<string> AsyncEnum()
    {
        await Task.CompletedTask;
        yield return "";
    }

    public Task<LlmLogprobResult> AskWithLogprobsAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
        => Task.FromResult(new LlmLogprobResult("", []));
}

/// <summary>
/// 记忆分级 + 上下文压缩的边界测试（t3）：批次选择、模型空/坏响应、同时间戳游标、原文保全。
/// 全部用合成数据 + 临时数据目录，不碰真实用户数据。
/// </summary>
public class MemoryCompressBoundaryTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly FeatureHost _host;
    private readonly HistoryStore _history;
    private readonly ScriptedLlm _llm = new();
    private readonly string _chatId = "owner";

    public MemoryCompressBoundaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-memtests-" + Guid.NewGuid().ToString("N"));
        _store = new DataStore(_root);
        var log = new AppLog();
        var config = new ConfigStore(_store, log).Load();
        _host = new FeatureHost
        {
            Store = _store,
            Config = config,
            Auth = new AuthService(_store, log),
            Log = log,
            Persona = new PersonaService(_store, Path.Combine(_root, "personas-bundled")),
        };
        _host.Set<ILlmClient>(_llm);
        _history = new HistoryStore(_host, new MediaStore(_store, log));
    }

    // ---------------- 合成数据 ----------------

    private long BaseT => DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

    /// <summary>往历史里追加 n 条文本消息；exceptSame 从这个下标起 burst 条共用同一毫秒（测同时间戳游标）。</summary>
    private long SeedHistory(int n, int start = 0, int count = int.MaxValue, int? exceptSame = null, int burst = 12)
    {
        var baseT = BaseT + start * 60_000L;
        var end = Math.Min(n, start + count);
        for (var i = start; i < end; i++)
        {
            var t = baseT + i * 60_000L;
            if (exceptSame is { } from && i >= from && i < from + burst) t = baseT + from * 60_000L;
            _history.Append(_chatId, new JsonObject
            {
                ["who"] = i % 2 == 0 ? "user" : "bot",
                ["kind"] = "text",
                ["text"] = $"旧消息{i}：约好{ i }点一起吃火锅",
                ["t"] = t,
            });
        }
        return baseT;
    }

    /// <summary>预写一份 memories.json（受控的 at/importance/source，测分级批次选择）。</summary>
    private void SeedMemoryItems(int n, Func<int, (double Importance, string Source, long At)> make, string[]? texts = null)
    {
        var items = new JsonArray();
        for (var i = 0; i < n; i++)
        {
            var (imp, source, at) = make(i);
            items.Add(new JsonObject
            {
                ["id"] = $"m{i:0000}",
                ["text"] = texts != null && i < texts.Length ? texts[i] : $"记忆{i}：主人喜欢第{i}种口味的火锅",
                ["about"] = "",
                ["aboutName"] = "",
                ["tags"] = new JsonArray(),
                ["importance"] = imp,
                ["source"] = source,
                ["chatId"] = "",
                ["at"] = at,
                ["hits"] = 0,
            });
        }
        DataStore.WriteJson(_store.PathIn("memories.json"), new JsonObject
        {
            ["rev"] = 1,
            ["items"] = items,
        });
    }

    private static long DaysAgo(double days) => DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();

    /// <summary>预写一个 chatlog 天文件（行格式对齐 ChatLog）。</summary>
    private void WriteChatLogDay(string day, params (string ChatId, string Who, string Text)[] lines)
    {
        var dir = _store.PathIn("chatlog");
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        foreach (var (chatId, who, text) in lines)
        {
            var line = new JsonObject
            {
                ["t"] = $"{day}T10:00:00.000Z",
                ["chatId"] = chatId,
                ["who"] = who,
                ["text"] = text,
            };
            sb.AppendLine(line.ToJsonString());
        }
        File.WriteAllText(Path.Combine(dir, day + ".jsonl"), sb.ToString());
    }

    private static string Yesterday => ChatLog.DayKey(DateTime.Now.AddDays(-1));
    private static string Today => ChatLog.DayKey();

    private ContextCompressor NewCompressor() => new(_host, _history);
    private MemoryService NewMemory() => new(_host);

    /// <summary>读会话槽位（含 parts / 覆盖锚点）。</summary>
    private JsonObject? Slot()
        => DataStore.ReadObject(_store.PathIn("compress.json"))["chats"] is JsonObject chats
            ? chats[_chatId] as JsonObject
            : null;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ================= 上下文压缩：批次选择 / 同时间戳游标 / 原文保全 =================

    [Fact]
    public async Task Compress_SameTimestampBacklog_MonotonicBatches_NoDupNoSkip()
    {
        // 80 条同一毫秒：可压区 50 条，老实现按 coversTo 时间戳卡边界会把 40 条之后的全漏掉
        SeedHistory(80, exceptSame: 0, burst: 80);
        _llm.Script.Enqueue("摘要一");
        _llm.Script.Enqueue("摘要二");

        var comp = NewCompressor();
        var r1 = await comp.RunOnceAsync();
        Assert.True((bool?)r1["ok"]);
        Assert.Equal(40, (int?)r1["summarized"]);

        var r2 = await comp.RunOnceAsync();
        Assert.True((bool?)r2["ok"]);
        Assert.Equal(10, (int?)r2["summarized"]); // 同毫秒剩余 10 条也要被压到

        var r3 = await comp.RunOnceAsync();
        Assert.Equal(0, (int?)r3["summarized"]);  // 没有积压了

        var slot = Slot();
        Assert.NotNull(slot);
        Assert.Equal(50, (int?)slot!["coversCount"]);               // 不重不漏：恰好 50
        Assert.Equal(2, (slot["parts"] as JsonArray)?.Count);
        Assert.Equal(50, (int?)slot["coversToPos"]);                // 锚点单调推进 40 → 50
        var merged = slot["summary"]!.ToString();
        Assert.True(merged.IndexOf("摘要一", StringComparison.Ordinal) < merged.IndexOf("摘要二", StringComparison.Ordinal));

        // 模型只见过两批原文：第一批是最旧的 40 条，第二批是紧跟的 10 条
        Assert.Equal(2, _llm.Prompts.Count);
        Assert.Contains("旧消息0：", _llm.Prompts[0]);
        Assert.Contains("旧消息39：", _llm.Prompts[0]);
        Assert.DoesNotContain("旧消息40：", _llm.Prompts[0]);
        Assert.Contains("旧消息40：", _llm.Prompts[1]);
        Assert.Contains("旧消息49：", _llm.Prompts[1]);

        // 原文保全：一条不少、抽查首尾文本原样
        Assert.Equal(80, _history.TotalOf(_chatId));
        var all = _history.Page(_chatId, 0, 100).Messages;
        Assert.Equal("旧消息0：约好0点一起吃火锅", all[0]?["text"]?.ToString());
        Assert.Equal("旧消息79：约好79点一起吃火锅", all[all.Count - 1]?["text"]?.ToString());
    }

    [Fact]
    public async Task Compress_SameTimestampBurst_StraddlingBoundary_StillCovered()
    {
        // 下标 37..48 共 12 条同一毫秒：第一轮压到第 40 条（边界落在 burst 中间），
        // 第二轮必须把 41..49（与已压边界同毫秒）一起压掉——老实现按 t > coversTo 全漏
        SeedHistory(80, exceptSame: 37, burst: 12);
        _llm.Script.Enqueue("摘要一");
        _llm.Script.Enqueue("摘要二");
        _llm.Script.Enqueue("摘要三");

        var comp = NewCompressor();
        Assert.Equal(40, (int?)(await comp.RunOnceAsync())["summarized"]);
        var r2 = await comp.RunOnceAsync();
        Assert.Equal(10, (int?)r2["summarized"]);
        Assert.Contains("旧消息40：", _llm.Prompts[1]); // 同毫秒的后半段没被吞
        Assert.Contains("旧消息49：", _llm.Prompts[1]);
        Assert.DoesNotContain("旧消息39：", _llm.Prompts[1]); // 已压过的不重复
        Assert.Equal(0, (int?)(await comp.RunOnceAsync())["summarized"]);
        Assert.Equal(50, (int?)Slot()!["coversCount"]);
        Assert.Equal(80, _history.TotalOf(_chatId));
    }

    [Fact]
    public async Task Compress_MultiPageWalk_AppendsBetweenRuns_BatchesStayAligned()
    {
        // 130 条跨两页（翻页游标会在同毫秒边界重复吐一条，靠 id 去重对齐）；
        // 第一轮后追加 60 条，后续批次仍严格从最旧未压处继续
        SeedHistory(130);
        _llm.Script.Enqueue("批一");
        var comp = NewCompressor();
        Assert.Equal(40, (int?)(await comp.RunOnceAsync())["summarized"]);
        Assert.Equal(40, (int?)Slot()!["coversToPos"]);
        Assert.Contains("旧消息0：", _llm.Prompts[0]);
        Assert.DoesNotContain("旧消息40：", _llm.Prompts[0]);

        // 中途追加 60 条新消息
        for (var i = 0; i < 60; i++)
        {
            _history.Append(_chatId, new JsonObject
            {
                ["who"] = "user",
                ["kind"] = "text",
                ["text"] = $"新消息{i}：后面补的",
                ["t"] = BaseT + (200 + i) * 60_000L,
            });
        }

        _llm.Script.Enqueue("批二");
        _llm.Script.Enqueue("批三");
        _llm.Script.Enqueue("批四");
        Assert.Equal(40, (int?)(await comp.RunOnceAsync())["summarized"]);
        Assert.Contains("旧消息40：", _llm.Prompts[1]); // 接着上一轮的最旧未压处
        Assert.Contains("旧消息79：", _llm.Prompts[1]);
        Assert.Equal(40, (int?)(await comp.RunOnceAsync())["summarized"]);
        Assert.Contains("旧消息80：", _llm.Prompts[2]);
        Assert.Equal(40, (int?)(await comp.RunOnceAsync())["summarized"]);
        Assert.Contains("旧消息120：", _llm.Prompts[3]); // 可压区到 旧消息129（region=160）
        Assert.Contains("旧消息129：", _llm.Prompts[3]);

        var r5 = await comp.RunOnceAsync();
        Assert.Equal(0, (int?)r5["summarized"]); // 剩下 30 条在保留窗口里
        var slot = Slot();
        Assert.Equal(160, (int?)slot!["coversCount"]);
        Assert.Equal(160, (int?)slot["coversToPos"]);
        Assert.Equal(4, (slot["parts"] as JsonArray)?.Count);
        Assert.Equal(190, _history.TotalOf(_chatId)); // 原文一条没动
    }

    [Fact]
    public async Task Compress_ModelEmptyResponse_SkipsKeepsRetryable()
    {
        SeedHistory(45);
        _llm.Script.Enqueue(null); // 空响应
        var comp = NewCompressor();
        var r1 = await comp.RunOnceAsync();
        Assert.False((bool?)r1["ok"]);
        Assert.True((bool?)r1["skipped"]);
        Assert.Null(Slot()); // 什么都不持久化，下一轮原样重试同一批

        _llm.Script.Enqueue("补上的摘要");
        var r2 = await comp.RunOnceAsync();
        Assert.True((bool?)r2["ok"]);
        Assert.Equal(15, (int?)r2["summarized"]);
        Assert.Equal(45, _history.TotalOf(_chatId));
    }

    [Fact]
    public async Task Compress_ModelThrows_SkipsOriginalIntact()
    {
        SeedHistory(45);
        _llm.Script.Enqueue(new HttpRequestException("连接炸了"));
        var comp = NewCompressor();
        var r1 = await comp.RunOnceAsync();
        Assert.False((bool?)r1["ok"]);
        Assert.Null(Slot());
        Assert.Equal(45, _history.TotalOf(_chatId));
        var msgs = _history.Page(_chatId, 0, 100).Messages;
        Assert.Equal("旧消息0：约好0点一起吃火锅", msgs[0]?["text"]?.ToString());

        _llm.Script.Enqueue("恢复后的摘要");
        Assert.Equal(15, (int?)(await comp.RunOnceAsync())["summarized"]);
    }

    [Fact]
    public async Task Compress_ModelRunawayOutput_Truncated()
    {
        SeedHistory(45);
        _llm.Script.Enqueue(new string('爆', 5000));
        var comp = NewCompressor();
        Assert.True((bool?)(await comp.RunOnceAsync())["ok"]);
        var slot = Slot();
        Assert.True(slot!["summary"]!.ToString().Length <= ContextCompressor.SummaryMaxChars);
    }

    [Fact]
    public async Task Compress_LegacySlot_MigratesIntoParts_NoCoverageGap()
    {
        // 旧版本只写了平铺字段（没有 coversToId/coversToPos/parts）：
        // 迁移成 parts[0]，接着从时间戳边界之后压，已覆盖的 40 条不重复
        var baseT = SeedHistory(80);
        var legacyCoversTo = baseT + 39 * 60_000L; // 旧数据声称压到了第 40 条
        DataStore.WriteJson(_store.PathIn("compress.json"), new JsonObject
        {
            ["chats"] = new JsonObject
            {
                [_chatId] = new JsonObject
                {
                    ["summary"] = "旧版摘要",
                    ["coversFrom"] = baseT,
                    ["coversTo"] = legacyCoversTo,
                    ["coversCount"] = 40,
                    ["at"] = DaysAgo(1),
                },
            },
        });

        _llm.Script.Enqueue("新版摘要");
        var comp = NewCompressor();
        // 可压区 = 80-30 = 50 条，旧覆盖已含前 40 → 本轮压 41..50 共 10 条
        var r = await comp.RunOnceAsync();
        Assert.True((bool?)r["ok"]);
        Assert.Equal(10, (int?)r["summarized"]);
        Assert.Contains("旧消息40：", _llm.Prompts[0]);
        Assert.Contains("旧消息49：", _llm.Prompts[0]);
        Assert.DoesNotContain("旧消息39：", _llm.Prompts[0]);

        var slot = Slot();
        Assert.Equal(2, (slot!["parts"] as JsonArray)?.Count); // 旧摘要并成第一段
        Assert.Equal(50, (int?)slot["coversCount"]);           // 40 + 10，无缺口无重叠
        Assert.Contains("旧版摘要", slot["summary"]!.ToString());
        Assert.Contains("新版摘要", slot["summary"]!.ToString());
        Assert.Equal(50, (int?)slot["coversToPos"]);
    }

    // ================= 记忆分级 =================

    /// <summary>走一遍巩固（无聊天日志），脚本喂 reflect + compress 应答，返回压缩结果。</summary>
    private async Task<JsonObject> ConsolidateForGrading(params object[] compressScript)
    {
        _llm.Script.Enqueue("{\"insights\":[],\"style\":[]}"); // reflect：什么都不总结
        foreach (var s in compressScript) _llm.Script.Enqueue(s);
        var mem = NewMemory();
        return await mem.ConsolidateAsync(includeToday: true, force: true);
    }

    [Fact]
    public async Task Grading_OldestFirst_BatchCappedAt40()
    {
        // 45 条未分级：注释口径是「拿最老的 40 条压一轮」，老实现却按时间倒序拿最新的
        SeedMemoryItems(45, i => (0.5, "tool", DaysAgo(30 - i * 0.5)));
        var compressed = await ConsolidateForGrading("[{\"i\":1,\"brief\":\"压\"}]");
        Assert.True((bool?)compressed["ok"]);

        var root = DataStore.ReadObject(_store.PathIn("memories.json"));
        var items = (root["items"] as JsonArray)!.OfType<JsonObject>()
            .OrderBy(m => (long)(m["at"]?.GetValue<double>() ?? 0))
            .ToList();
        // 45 条里按时间正序取前 40（最旧的）压掉，最新的 5 条留给下一轮
        for (var i = 0; i < 45; i++)
        {
            var grade = items[i]["grade"]?.ToString() ?? "";
            if (i < 40) Assert.NotEqual("", grade);
            else Assert.Equal("", grade);
        }
        var lastCompress = root["lastCompress"] as JsonObject;
        Assert.Equal(40, (int?)lastCompress!["made"]);
    }

    [Fact]
    public async Task Grading_ImportanceThresholdBoundaries()
    {
        // 分级档位边界：0.7 全文保级 / 0.3 缩 brief / 0.05 只留 kw / 再低 drop；
        // 24h 内的经历不压，过了一天的经历照压
        double[] imps = [0.7, 0.69, 0.3, 0.29, 0.05, 0.049];
        SeedMemoryItems(8, i => i switch
        {
            < 6 => (imps[i], "tool", DaysAgo(5)),
            6 => (0.5, "event", DaysAgo(0.01)),
            _ => (0.5, "event", DaysAgo(2)),
        }, texts: DefaultTexts(8, (6, "刚发生的经历"), (7, "两天前的经历")));

        _llm.Script.Enqueue("{\"insights\":[],\"style\":[]}");
        _llm.Script.Enqueue("[{\"i\":1,\"brief\":\"压\"},{\"i\":2,\"brief\":\"压\"},{\"i\":3,\"brief\":\"压\"},"
                            + "{\"i\":4,\"brief\":\"压\"},{\"i\":5,\"brief\":\"压\"},{\"i\":6,\"brief\":\"压\"},"
                            + "{\"i\":7,\"brief\":\"压\"},{\"i\":8,\"brief\":\"压\"}]");
        var mem = NewMemory();
        var r = await mem.ConsolidateAsync(includeToday: true, force: true);
        Assert.True((bool?)r["ok"]);

        var items = (DataStore.ReadObject(_store.PathIn("memories.json"))["items"] as JsonArray)!
            .OfType<JsonObject>().ToDictionary(m => m["text"]!.ToString());

        Assert.Equal("full", items["记忆0：主人喜欢第0种口味的火锅"]["grade"]?.ToString());
        Assert.Equal("brief", items["记忆1：主人喜欢第1种口味的火锅"]["grade"]?.ToString());
        Assert.Equal("brief", items["记忆2：主人喜欢第2种口味的火锅"]["grade"]?.ToString());
        Assert.Equal("kw", items["记忆3：主人喜欢第3种口味的火锅"]["grade"]?.ToString());
        Assert.Equal("kw", items["记忆4：主人喜欢第4种口味的火锅"]["grade"]?.ToString());
        Assert.Equal("drop", items["记忆5：主人喜欢第5种口味的火锅"]["grade"]?.ToString());
        Assert.Equal("", items["刚发生的经历"]["grade"]?.ToString() ?? ""); // 24h 内不压
        Assert.NotEqual("", items["两天前的经历"]["grade"]?.ToString() ?? "");

        // 原文保全：分级不动 text，缩写放别的字段
        Assert.Equal("记忆1：主人喜欢第1种口味的火锅", items["记忆1：主人喜欢第1种口味的火锅"]["text"]!.ToString());
    }

    /// <summary>默认合成文本（记忆{i}：主人喜欢第{i}种口味的火锅），指定下标换文案。</summary>
    private static string[] DefaultTexts(int n, params (int Index, string Text)[] overrides)
    {
        var list = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var hit = overrides.FirstOrDefault(o => o.Index == i);
            list.Add(hit.Text ?? $"记忆{i}：主人喜欢第{i}种口味的火锅");
        }
        return [.. list];
    }

    [Fact]
    public async Task Grading_ModelEmpty_AbortsRound_ItemsUntouchedThenRetries()
    {
        // 模型空响应：一条可用缩写都拿不到 → 本轮不写任何分级。分级一旦写上就不会再重压，
        // 不能把整批记忆静默降级成本地截断（与上下文压缩「空响应跳过下轮重试」同一口径）。
        // 原文/条目分毫不动，记 failed 退避标记；恢复后重试照常分级。
        SeedMemoryItems(3, i => (i == 2 ? 0.1 : 0.5, "tool", DaysAgo(5)),
            texts: ["这是一条超过二十四个字的记忆内容用来验证空响应不降级行为", "短的", "另一条同样超过二十四个字长度的记忆文本继续验证空响应不降级"]);
        _llm.Script.Enqueue("{\"insights\":[],\"style\":[]}"); // reflect
        _llm.Script.Enqueue((object?)null);                    // 分级：模型空响应
        var mem = NewMemory();
        var r1 = await mem.ConsolidateAsync(includeToday: true, force: true);
        Assert.True((bool?)r1["ok"]);                          // 整理本身不受影响
        Assert.False((bool?)r1["compressed"]?["ok"] ?? true);  // 分级这轮跳过
        Assert.True((bool?)r1["compressed"]?["skipped"]);

        var items1 = (DataStore.ReadObject(_store.PathIn("memories.json"))["items"] as JsonArray)!
            .OfType<JsonObject>().ToDictionary(m => m["text"]!.ToString());
        Assert.All(items1.Values, m => Assert.True(string.IsNullOrEmpty(m["grade"]?.ToString()), "空响应不许写分级"));
        var root1 = DataStore.ReadObject(_store.PathIn("memories.json"));
        Assert.True((bool?)root1["lastCompress"]?["failed"]);  // 失败退避标记
        Assert.Equal(0, (int?)root1["lastCompress"]?["made"]);
        // 原文保全
        Assert.Equal("这是一条超过二十四个字的记忆内容用来验证空响应不降级行为",
            items1["这是一条超过二十四个字的记忆内容用来验证空响应不降级行为"]["text"]!.ToString());

        // 模型恢复（force 绕过退避窗口）：briefs 按序号对齐写上，失败标记清掉
        _llm.Script.Enqueue("{\"insights\":[],\"style\":[]}");
        _llm.Script.Enqueue("[{\"i\":1,\"brief\":\"缩一\"},{\"i\":2,\"brief\":\"缩二\"},{\"i\":3,\"brief\":\"缩三\"}]");
        var r2 = await mem.ConsolidateAsync(includeToday: true, force: true);
        Assert.True((bool?)r2["ok"]);
        var items2 = (DataStore.ReadObject(_store.PathIn("memories.json"))["items"] as JsonArray)!
            .OfType<JsonObject>().ToDictionary(m => m["text"]!.ToString());
        Assert.Equal("brief", items2["这是一条超过二十四个字的记忆内容用来验证空响应不降级行为"]["grade"]?.ToString());
        Assert.Equal("缩一", items2["这是一条超过二十四个字的记忆内容用来验证空响应不降级行为"]["brief"]?.ToString());
        var kw1 = items2["另一条同样超过二十四个字长度的记忆文本继续验证空响应不降级"];
        Assert.Equal("kw", kw1["grade"]?.ToString());
        Assert.Equal("缩三", kw1["kw"]?.ToString()); // 模型给的缩写优先于本地截断
        Assert.Null((bool?)DataStore.ReadObject(_store.PathIn("memories.json"))["lastCompress"]?["failed"]);
    }

    [Fact]
    public async Task Grading_ModelGarbageIndices_IgnoredOrLastWins()
    {
        SeedMemoryItems(2, i => (0.5, "tool", DaysAgo(5)),
            texts: ["第一条要压的记忆", "第二条要压的记忆"]);
        // i=0 / 越界 / 重复：0 与 99 忽略，i=2 重复时后者覆盖前者
        var bad = "[{\"i\":0,\"brief\":\"无效\"},{\"i\":99,\"brief\":\"越界\"},"
                  + "{\"i\":2,\"brief\":\"草稿\"},{\"i\":2,\"brief\":\"定稿\"},{\"i\":1,\"brief\":\"第一条压好\"}]";
        var r = await ConsolidateForGrading(bad);
        Assert.True((bool?)r["ok"]);

        var items = (DataStore.ReadObject(_store.PathIn("memories.json"))["items"] as JsonArray)!
            .OfType<JsonObject>().ToDictionary(m => m["text"]!.ToString());
        Assert.Equal("第一条压好", items["第一条要压的记忆"]["brief"]?.ToString());
        Assert.Equal("定稿", items["第二条要压的记忆"]["brief"]?.ToString());
    }

    // ================= 巩固偏移 / 标记重试 =================

    [Fact]
    public async Task Consolidate_ModelFailure_MidDays_OffsetKeptAndRetryable()
    {
        // 两天日志，各 6 行都出自用户：第一轮第 1 天提炼成功、第 2 天模型空响应；
        // 偏移只推进第 1 天，第 2 天不标已处理；第二轮模型恢复只补第 2 天，第 1 天不重复提炼
        WriteChatLogDay(Yesterday,
            (_chatId, "user", "昨天的事一"), (_chatId, "user", "昨天的事二"), (_chatId, "user", "昨天的事三"),
            (_chatId, "bot", "好的"), (_chatId, "user", "昨天的事四"), (_chatId, "user", "昨天的事五"));
        WriteChatLogDay(Today,
            (_chatId, "user", "今天的事一"), (_chatId, "user", "今天的事二"), (_chatId, "user", "今天的事三"),
            (_chatId, "user", "今天的事四"), (_chatId, "user", "今天的事五"), (_chatId, "user", "今天的事六"));

        _llm.Script.Enqueue("[{\"text\":\"主人昨天聊了火锅\",\"importance\":0.5,\"tags\":[],\"about\":\"\"}]");
        _llm.Script.Enqueue(null); // 第 2 天模型空响应
        var mem = NewMemory();
        var r1 = await mem.ConsolidateAsync(includeToday: true, force: true);
        Assert.False((bool?)r1["ok"]); // 整轮当失败
        Assert.Contains("重试", (string?)r1["message"]);

        var root1 = DataStore.ReadObject(_store.PathIn("memories.json"));
        var processed1 = root1["processed"] as JsonObject;
        Assert.Equal(6, (int?)processed1![Yesterday]);
        Assert.Equal(0, (int?)processed1[Today]); // 今天没被标成已处理（失败组只承认组前进度）
        Assert.Equal(1, (root1["items"] as JsonArray)?.Count); // 第 1 天的 1 条已入库

        // 第二轮：模型恢复，只补第 2 天
        _llm.Script.Enqueue("[{\"text\":\"主人今天买了票\",\"importance\":0.5,\"tags\":[],\"about\":\"\"}]");
        _llm.Script.Enqueue("{\"insights\":[],\"style\":[]}"); // reflect
        _llm.Script.Enqueue("[]"); // 分级：回空数组 → 本轮跳过不写分级（不影响整理 ok）
        var r2 = await mem.ConsolidateAsync(includeToday: true, force: true);
        Assert.True((bool?)r2["ok"]);

        // 第 1 天只提炼过一次：prompt 里「昨天的事一」只出现一次
        var day1Prompts = _llm.Prompts.Count(p => p.Contains("昨天的事一"));
        Assert.Equal(1, day1Prompts);
        Assert.Contains("今天的事一", _llm.Prompts[2]); // 第 2 轮第 1 个调用就是第 2 天的重试

        var root2 = DataStore.ReadObject(_store.PathIn("memories.json"));
        Assert.Equal(6, (int?)((root2["processed"] as JsonObject)!)[Today]);
        Assert.Equal(2, (root2["items"] as JsonArray)?.Count); // 两天各 1 条，无重复
    }

    [Fact]
    public async Task Tick_ConsolidateMarker_RevertsOnFailureAndRetries()
    {
        // 到点档先写标记；整理失败（模型空响应）就摘掉标记，下个 tick 重试成功后标记恢复
        // 凌晨/上午的到点档只整理到昨天（includeToday = 时段≥12:00），所以夹具写昨天的日志，任何时刻跑都成立
        WriteChatLogDay(Yesterday,
            (_chatId, "user", "昨天的事一"), (_chatId, "user", "昨天的事二"), (_chatId, "user", "昨天的事三"));
        var slotMinutes = new List<string> { DateTime.Now.ToString("HH:mm"), DateTime.Now.AddMinutes(1).ToString("HH:mm") };
        var times = new JsonArray();
        foreach (var s in slotMinutes) times.Add(s);
        _host.Config.Save(new JsonObject { ["memory"] = new JsonObject { ["consolidateTimes"] = times.DeepClone() } });

        var mem = NewMemory();
        _llm.Script.Enqueue(null); // 整理失败
        await mem.TickAsync();
        var root1 = DataStore.ReadObject(_store.PathIn("memories.json"));
        var runs1 = root1["runs"] as JsonObject;
        Assert.True(runs1 is null || runs1.Count == 0); // 标记被摘掉，会重试

        _llm.Script.Enqueue("[{\"text\":\"主人昨天提到火锅\",\"importance\":0.5,\"tags\":[],\"about\":\"\"}]");
        _llm.Script.Enqueue("{\"insights\":[],\"style\":[]}");
        _llm.Script.Enqueue("[]");
        await mem.TickAsync();
        var root2 = DataStore.ReadObject(_store.PathIn("memories.json"));
        var runs2 = root2["runs"] as JsonObject;
        Assert.NotNull(runs2);
        Assert.Contains(runs2!.Select(p => p.Value?.ToString()), v => v == ChatLog.DayKey()); // 成功后标记在
        Assert.Equal(1, (root2["items"] as JsonArray)?.Count);
    }

    // ================= 召回日志持久化 =================

    [Fact]
    public void RecallLog_PersistsAcrossRestart_AndClearWorks()
    {
        var mem1 = NewMemory();
        mem1.AddInternal("主人喜欢吃火锅", "owner", "主人", ["吃的"], 0.8);
        mem1.AddInternal("主人养了一只猫", "owner", "主人", ["宠物"], 0.8);
        mem1.RecallInternal("火锅", "owner", where: "test");
        Assert.Equal(1, mem1.Recalls().Count);

        // 新实例 = 模拟重启：日志从 memories.json 恢复
        var mem2 = NewMemory();
        var log2 = mem2.Recalls();
        Assert.Equal(1, log2.Count);
        Assert.Equal("火锅", log2[0]?["query"]?.ToString());
        Assert.True((DataStore.ReadObject(_store.PathIn("memories.json"))["recalls"] as JsonArray)!.Count == 1);

        mem2.ClearRecalls();
        var mem3 = NewMemory();
        Assert.Empty(mem3.Recalls());
    }

    [Fact]
    public void RecallLog_CapsAtTwentyEntries()
    {
        var mem = NewMemory();
        for (var i = 0; i < 25; i++)
            mem.RecallInternal($"查询{i}", "owner", where: "test");
        Assert.Equal(20, mem.Recalls().Count);
        var persisted = DataStore.ReadObject(_store.PathIn("memories.json"))["recalls"] as JsonArray;
        Assert.Equal(20, persisted!.Count);
        // 最旧的被挤掉，最新在第 0 位
        Assert.Contains("查询24", mem.Recalls()[0]?["query"]?.ToString());
    }
}
