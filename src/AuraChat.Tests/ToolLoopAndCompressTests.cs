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
using Xunit;

namespace AuraChat.Tests;

/// <summary>自带的假模型：把请求记下来，按脚本回话（工具循环测试用）。</summary>
internal sealed class FakeLlm : ILlmClient
{
    public List<JsonObject> SeenBodies { get; } = [];
    public Queue<JsonObject> Replies { get; } = new();
    public string LastPlainReply = "好的呀";

    /// <summary>AskAsync 收到的 options（v3：验真提示词里有没有「现在」这一行）。</summary>
    public List<LlmOptions> SeenOptions { get; } = [];

    public Task<string> AskAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        SeenOptions.Add(options);
        return Task.FromResult(LastPlainReply);
    }

    public async Task<JsonObject> AskMessageAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages,
        JsonArray? tools = null, JsonArray? toolHistory = null, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var body = new JsonObject
        {
            ["toolsGiven"] = tools != null && tools.Count > 0,
            ["historyLen"] = toolHistory?.Count ?? 0,
            ["lastUserText"] = messages.Count > 0 ? messages[^1].Content : "",
        };
        SeenBodies.Add(body);
        return Replies.Count > 0 ? Replies.Dequeue() : new JsonObject { ["content"] = LastPlainReply };
    }

    public IAsyncEnumerable<string> StreamAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
        => AsyncEnum();

    private async IAsyncEnumerable<string> AsyncEnum()
    {
        await Task.CompletedTask;
        yield return LastPlainReply;
    }

    public Task<LlmLogprobResult> AskWithLogprobsAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
        => Task.FromResult(new LlmLogprobResult("", []));
}

/// <summary>C2/C3/工具循环：压缩器安全（不删原文、失败跳过）、工具循环能跑通一轮。</summary>
public class ToolLoopAndCompressTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly FeatureHost _host;
    private readonly HistoryStore _history;
    private readonly FakeLlm _llm = new();
    private readonly ToolRegistry _tools;
    private readonly string _chatId = "owner";

    public ToolLoopAndCompressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-loop-tests-" + Guid.NewGuid().ToString("N"));
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
        _history = new HistoryStore(_host, new MediaStore(_store, log));
        _tools = new ToolRegistry(_host);
    }

    private void Seed(int n, int daysAgo = 10)
    {
        var baseT = DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds();
        for (var i = 0; i < n; i++)
        {
            _history.Append(_chatId, new JsonObject
            {
                ["who"] = i % 2 == 0 ? "user" : "bot",
                ["kind"] = "text",
                ["text"] = $"旧消息{i}：约好周末一起去吃火锅",
                ["t"] = baseT + i * 60_000,
            });
        }
    }

    [Fact]
    public void Compressor_PreservesOriginalText()
    {
        Seed(5);
        var comp = new ContextCompressor(_host, _history);
        // 模型没配（ILlmClient 未注册）→ 跳过不炸，原文分毫不动
        var r = comp.RunOnceAsync(default).GetAwaiter().GetResult();
        Assert.False((bool?)r["ok"] ?? true);
        var msgs = _history.Page(_chatId, 0, 100).Messages;
        Assert.Equal(5, msgs.Count);
    }

    [Fact]
    public void Compressor_SummaryOf_EmptyBeforeRun()
    {
        var comp = new ContextCompressor(_host, _history);
        Assert.Null(comp.SummaryOf(_chatId));
    }

    [Fact]
    public void Compressor_Disabled_Skips()
    {
        Seed(5);
        _host.Config.Save(new JsonObject { ["context"] = new JsonObject { ["compress"] = false } });
        var comp = new ContextCompressor(_host, _history);
        var r = comp.RunOnceAsync(default).GetAwaiter().GetResult();
        Assert.True((bool?)r["skipped"]);
        Assert.Equal(5, _history.Page(_chatId, 0, 100).Messages.Count);
    }

    [Fact]
    public async Task ToolLoop_OneRound_ExecutesToolAndReturnsFinalText()
    {
        // 时钟工具（真实现）+ 模型第一轮回 tool_calls，第二回给正文
        _tools.Register(ToolSchemas.CheckClock, (_, _) => Task.FromResult("现在是 2026-11-04 14:00:00 日曜日（下午，14 点）。"));
        _llm.Replies.Enqueue(new JsonObject
        {
            ["content"] = "",
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "call-1",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "check_clock", ["arguments"] = "{}" },
            }),
        });
        _llm.Replies.Enqueue(new JsonObject { ["content"] = "现在是下午两点啦。" });

        var loop = new ToolLoop(_llm, _tools, new AppLog().Child("t"));
        var reply = await loop.RunAsync(new LlmOptions(),
            [new LlmMessage("user", "现在几点了？")], default);

        Assert.Equal("现在是下午两点啦。", reply);
        // 第一轮给了 tools，第二轮 toolHistory 里有 1 条 assistant + 1 条 tool
        Assert.True((bool?)_llm.SeenBodies[0]["toolsGiven"]);
        Assert.Equal(2, (int?)_llm.SeenBodies[1]["historyLen"]);
    }

    [Fact]
    public async Task ToolLoop_NoTools_FallsBackToPlainAsk()
    {
        var empty = new ToolRegistry(_host, declareAll: false);
        var loop = new ToolLoop(_llm, empty, new AppLog().Child("t"));
        var reply = await loop.RunAsync(new LlmOptions(), [new LlmMessage("user", "嗨")], default);
        Assert.Equal("好的呀", reply);
        Assert.Empty(_llm.SeenBodies);
    }

    [Fact]
    public void ClockAndHistoryTools_Registered_InChatHub()
    {
        // ChatHub.StartAsync 里注册 check_clock / check_history；这里验证 schema 与注册后 Count
        Assert.Equal("check_clock", ToolSchemas.CheckClock);
        Assert.Equal("check_history", ToolSchemas.CheckHistory);
        Assert.Contains(_tools.All(), t => t.Name == "check_clock");
        Assert.Contains(_tools.All(), t => t.Name == "check_history");
    }

    [Fact]
    public void CheckHistory_KeywordAndImages()
    {
        Seed(3, daysAgo: 2);
        // 一条带图消息
        _history.Append(_chatId, new JsonObject
        {
            ["who"] = "user", ["kind"] = "image", ["text"] = "[图片]",
            ["t"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
            ["mediaId"] = "fake-media-id.png", ["mediaMime"] = "image/png",
        });

        var hub = new ChatHub(_host);
        hub.StartAsync(default).GetAwaiter().GetResult();

        // 通过反射调 private CheckHistoryAsync
        var mi = hub.GetType().GetMethod("CheckHistoryAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var r1 = ((Task<string>)mi.Invoke(hub, [new JsonObject { ["keyword"] = "火锅" }, default])!).GetAwaiter().GetResult();
        Assert.Contains("火锅", r1);
        var r2 = ((Task<string>)mi.Invoke(hub, [new JsonObject { ["wantImages"] = true }, default])!).GetAwaiter().GetResult();
        Assert.Contains("fake-media-id.png", r2);
        hub.StopAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
