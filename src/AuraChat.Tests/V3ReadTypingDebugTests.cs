using System.Text.Json.Nodes;
using AuraChat.Core;
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
using AuraChat.Server.Routes;
using Xunit;

namespace AuraChat.Tests;

/// <summary>把 SSE 事件记下来（测试里没有真浏览器）。</summary>
internal sealed class CaptureEmitter : ISseEmitter
{
    private readonly List<(string Type, JsonObject Payload)> _events = [];

    public void Emit(string type, JsonObject payload)
    {
        lock (_events) _events.Add((type, (JsonObject)payload.DeepClone()));
    }

    public void Emit(string type, object payload) => Emit(type, payload as JsonObject ?? new JsonObject());

    public List<(string Type, JsonObject Payload)> Snapshot()
    {
        lock (_events) return [.. _events];
    }

    public List<string> TypingStates() => Snapshot()
        .Where(e => e.Type == "typing")
        .Select(e => (bool?)e.Payload["on"] == true ? "on" : "off")
        .ToList();
}

/// <summary>
/// v3 回归：已读水位只前推（upToT）、typing 前移与 off 收敛、调试窗口与真提示词同一份开头。
/// </summary>
public class V3ReadTypingDebugTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly FeatureHost _host;
    private readonly HistoryStore _history;
    private readonly CaptureEmitter _sse = new();
    private const string ChatId = "owner";

    public V3ReadTypingDebugTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-v3-tests-" + Guid.NewGuid().ToString("N"));
        _store = new DataStore(_root);
        var log = new AppLog();
        _host = new FeatureHost
        {
            Store = _store,
            Config = new ConfigStore(_store, log).Load(),
            Auth = new AuthService(_store, log),
            Log = log,
            Persona = new PersonaService(_store, Path.Combine(_root, "personas-bundled")),
            Emitter = _sse,
        };
        _history = new HistoryStore(_host, new MediaStore(_store, log));
        _host.Set<IHistoryStore>(_history);
    }

    private void BotSays(long t) => _history.Append(ChatId, new JsonObject
    {
        ["who"] = "bot",
        ["kind"] = "text",
        ["text"] = "在的",
        ["t"] = t,
    });

    private static async Task<bool> WaitFor(Func<bool> cond, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(25);
        }
        return cond();
    }

    // ---------------------------------------------------------------- 已读水位（POST api/read 的 upToT）

    [Fact]
    public async Task MarkRead_UpToT_AdvancesOnly_NeverRegresses_ClampedToNow()
    {
        // 先有一轮已读基线（lastReadAt=0 时 Unread 按既有语义直接返回 0）
        var (_, baseline) = await _history.MarkReadAsync(ChatId);
        Assert.True(baseline > 0);

        await Task.Delay(10);
        var t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BotSays(t1);
        await Task.Delay(10);
        var t2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BotSays(t2);

        // ① 只报到第一条为止：水位推进到 t1，两条都还没读
        var (unreadBefore, readAt) = await _history.MarkReadAsync(ChatId, t1);
        Assert.Equal(2, unreadBefore);
        Assert.Equal(t1, readAt);
        Assert.Equal(t1, _history.LastReadAt(ChatId));
        Assert.Equal(1, _history.Unread(ChatId));

        // ② 回退请求：水位不许倒退，未读也不许变多
        var (_, back) = await _history.MarkReadAsync(ChatId, t1 - 60_000);
        Assert.Equal(t1, back);
        Assert.Equal(1, _history.Unread(ChatId));

        // ③ 报一个未来时间点：夹到服务器现在（≥ t2），未读清零
        var (_, future) = await _history.MarkReadAsync(ChatId, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
        Assert.True(future >= t2);
        Assert.True(future <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000);
        Assert.Equal(0, _history.Unread(ChatId));

        // ④ 缺省 upToT ＝ 现在（老客户端不带 body 的行为不变）
        var (_, plain) = await _history.MarkReadAsync(ChatId);
        Assert.True(plain >= future);
        Assert.True(plain <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000);
    }

    // ---------------------------------------------------------------- typing：入口点亮、四条路径都收掉

    [Fact]
    public async Task Typing_LitAtEntry_And_Cleared_WhenLlmMissing()
    {
        _host.Config.Save(new JsonObject { ["merge"] = new JsonObject { ["enabled"] = false } });
        var hub = new ChatHub(_host);   // 不注册 ILlmClient → RespondAsync 走「模型还没配好」那条路径

        var ok = await hub.OnUserMessageAsync(new UserMessage(ChatId, AuraChatInfo.OwnerId, "主人", "在吗", new JsonArray(), null, ""));
        Assert.True(ok);
        Assert.Equal(new[] { "on" }, _sse.TypingStates());   // 入口就点亮，不再等模型调用前

        // 合并等待关掉了 → 很快走到 llm 缺省，off 必须发出来（修之前这条路径直接 return，typing 常亮）
        Assert.True(await WaitFor(() => _sse.TypingStates().Count >= 2, 3000), "llm 缺省路径没有收掉 typing");
        Assert.Equal(new[] { "on", "off" }, _sse.TypingStates());
        await hub.StopAsync();
    }

    [Fact]
    public async Task Typing_EmptyContent_OnThenOff_NoRound()
    {
        var hub = new ChatHub(_host);
        var ok = await hub.OnUserMessageAsync(new UserMessage(ChatId, AuraChatInfo.OwnerId, "主人", "   ", new JsonArray(), null, ""));
        Assert.False(ok);
        Assert.Equal(new[] { "on", "off" }, _sse.TypingStates());
        await hub.StopAsync();
    }

    [Fact]
    public async Task Typing_Cleared_AfterSuccessfulRound()
    {
        _host.Config.Save(new JsonObject { ["merge"] = new JsonObject { ["enabled"] = false } });
        var llm = new FakeLlm { LastPlainReply = "在的呀" };
        _host.Set<ILlmClient>(llm);
        var hub = new ChatHub(_host);

        await hub.OnUserMessageAsync(new UserMessage(ChatId, AuraChatInfo.OwnerId, "主人", "在吗", new JsonArray(), null, ""));
        Assert.True(await WaitFor(() => _sse.TypingStates().Count >= 2, 5000), "成功路径没有收掉 typing");
        Assert.Equal(new[] { "on", "off" }, _sse.TypingStates());
        await hub.StopAsync();
    }

    // ---------------------------------------------------------------- 调试窗口 = 真提示词

    [Fact]
    public async Task DebugSystemParts_HeadIsTheSameAsRealPromptHead()
    {
        var parts = await DebugRoutes.BuildSystemPartsAsync(_host);
        var head = ChatHub.PromptHeadParts(_host, "private");

        Assert.Equal(head.Count, parts.Count);   // 没塞别的段时，窗口里就是这三段
        var names = parts.Select(p => p!["name"]!.GetValue<string>()).ToArray();
        var texts = parts.Select(p => p!["text"]!.GetValue<string>()).ToArray();
        Assert.Equal(head.Select(h => h.Name).ToArray(), names);
        // 前两段逐字一致；「现在」带时间，逐字比会撞分钟边界，比格式和同源性即可
        Assert.Equal(head[0].Text, texts[0]);
        Assert.Equal(head[1].Text, texts[1]);

        // 关键：调试窗口里必须有「现在」这一行（修之前自己拼 systemParts，漏了它）
        Assert.Equal("现在", names[2]);
        Assert.StartsWith("【现在】", texts[2]);
        Assert.StartsWith("【现在】", head[2].Text);
    }

    [Fact]
    public async Task RealRound_SystemPrompt_StartsWithTheSameHead()
    {
        _host.Config.Save(new JsonObject { ["merge"] = new JsonObject { ["enabled"] = false } });
        var llm = new FakeLlm();
        _host.Set<ILlmClient>(llm);
        var hub = new ChatHub(_host);

        await hub.OnUserMessageAsync(new UserMessage(ChatId, AuraChatInfo.OwnerId, "主人", "在吗", new JsonArray(), null, ""));
        Assert.True(await WaitFor(() => llm.SeenOptions.Count > 0, 5000), "这轮没走到模型调用");

        var system = llm.SeenOptions[0].System ?? "";
        var head = ChatHub.PromptHeadParts(_host, "private");
        Assert.StartsWith(head[0].Text + "\n\n" + head[1].Text + "\n\n", system);
        Assert.Contains("【现在】", system);
        await hub.StopAsync();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
