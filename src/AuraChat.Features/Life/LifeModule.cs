using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Runtime;
using AuraChat.Features.Chat;

namespace AuraChat.Features.Life;

/// <summary>
/// Life 功能装配（对应原 features/index.js 里记忆 / 心情 / 缺口 / 技能部分）：
/// 建服务、挂接口、把工具执行函数接上、摆状态面板和测试按钮。
/// ChatHub / 60 秒 ticker 由 AppHost 负责启停（TickLoop 由 ChatHub 自己带）。
/// </summary>
public static class LifeModule
{
    public static GapPool Create(FeatureHost host)
    {
        var gaps = new GapPool(host.Store, () => host.Cfg, host.Log.Child("gaps"));
        var memory = new MemoryService(host);
        var lifeState = new LifeStateService(host);
        var mood = new MoodService(host, gaps);
        var skills = new SkillService(host.Store, host.Log.Child("skills"));

        host.Set(gaps);
        host.Set<IMemoryService>(memory);
        host.Set(lifeState);
        host.Set<IMoodService>(mood);
        host.Set(skills);

        RegisterTools(host, memory, gaps, skills);

        // 状态面板：记忆 / 心情 / 生命状态 / 心事
        host.AddStatus(new StatusProvider("记忆", () => memory.Stats()));
        host.AddStatus(new StatusProvider("心情", () => mood.State()));
        host.AddStatus(new StatusProvider("生命状态", () => lifeState.StateSnapshot()));
        host.AddStatus(new StatusProvider("心事", () => new JsonObject
        {
            ["enabled"] = gaps.Enabled,
            ["open"] = new JsonArray(gaps.Open(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Select(i => (JsonNode)i.DeepClone()).ToArray()),
        }));

        // 测试按钮
        host.AddTest(new TestAction("记忆：跑一遍整理", () => memory.ConsolidateAsync(true, true)));
        host.AddTest(new TestAction("记忆：清空召回日志", () =>
        {
            memory.ClearRecalls();
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }));
        host.AddTest(new TestAction("心情：恢复出厂", () =>
        {
            mood.Reset();
            return Task.FromResult(new JsonObject { ["ok"] = true, ["message"] = "心情已清零" });
        }));
        host.AddTest(new TestAction("生命状态：重置为基线", () =>
        {
            lifeState.Reset();
            return Task.FromResult(new JsonObject { ["ok"] = true, ["message"] = "生命状态已重置为基线" });
        }));
        host.AddTest(new TestAction("心事：清空", () =>
        {
            gaps.Clear();
            return Task.FromResult(new JsonObject { ["ok"] = true, ["message"] = "心里的缺口都放下了" });
        }));

        // system prompt 上下文：技能目录
        host.AddContext(new SkillContext(skills));

        return gaps;
    }

    /// <summary>AppHost 每分钟调一次：记忆整理到点跑（MemoryService.TickAsync）。</summary>
    public static async Task TickAsync(FeatureHost host, CancellationToken ct)
    {
        if (host.Get<MemoryService>() is not { } memory) return;
        await memory.TickAsync(ct).ConfigureAwait(false);
    }

    // ---------- 工具执行函数（schema 早已声明，这里接上能跑的实现） ----------

    private static void RegisterTools(FeatureHost host, MemoryService memory, GapPool gaps, SkillService skills)
    {
        if (host.Get<IToolRegistry>() is not { } tools) return;

        tools.Register(ToolSchemas.Define(ToolSchemas.Remember, (args, ct) =>
        {
            var text = JsonUtil.Str(args["text"], "").Trim();
            if (text.Length == 0) return Task.FromResult("没说要记什么。");
            var about = JsonUtil.Str(args["about"], "").Trim();
            var importance = Math.Clamp(JsonUtil.Num(args["importance"], 0.5), 0, 1);
            var item = memory.AddAbout(text, importance, about, "", null);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Task.FromResult(item["id"] is null
                ? "这条和已有的记忆重了，没重复记。"
                : $"记下了（{MemoryText.AgoText(MemoryText.At(item), now)}）：{text}");
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.RecallMemory, (args, ct) =>
        {
            var query = JsonUtil.Str(args["query"], "").Trim();
            return memory.RecallAsync(AuraChatInfo.OwnerId, query, ct);
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.GapList, (args, ct) =>
        {
            var list = gaps.List();
            if (list.Count == 0) return Task.FromResult("心里干干净净，没挂着什么事。");
            var lines = new List<string> { $"心里挂着 {list.Count} 件事：" };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var g in list.OfType<JsonObject>())
            {
                lines.Add($"- {JsonUtil.Str(g["text"], "")}（张力 {gaps.TensionOf(g, now):0}）");
            }
            return Task.FromResult(string.Join('\n', lines));
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.GapResolve, (args, ct) =>
        {
            var text = JsonUtil.Str(args["text"], "").Trim();
            if (text.Length == 0) return Task.FromResult("没说哪件事。");
            var hit = gaps.ResolveByText(text);
            return Task.FromResult(hit is null
                ? "心里没找到对得上的事。"
                : $"划掉了：{JsonUtil.Str(hit["text"], "")}");
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.UseSkill, (args, ct) =>
        {
            var name = JsonUtil.Str(args["name"], "").Trim();
            if (name.Length == 0) return Task.FromResult("没说要看哪个技能。");
            var skill = skills.Read(name);
            return Task.FromResult(skill is null
                ? $"没有这个技能：{name}。"
                : $"{skill.Description}");
        }));
    }

    // ---------- 小实现：状态 / 测试 / 上下文 ----------

    private sealed class StatusProvider(string key, Func<JsonNode?> read) : IStatusProvider
    {
        public string Key { get; } = key;
        public JsonNode? Snapshot() => read();
    }

    private sealed class TestAction(string name, Func<Task<JsonObject>> run) : ITestAction
    {
        public string Name { get; } = name;
        public Task<JsonObject> RunAsync() => run();
    }

    private sealed class SkillContext(SkillService skills) : IContextLines
    {
        public IReadOnlyList<string> ContextLines()
        {
            var line = skills.ContextLine();
            return line.Length == 0 ? [] : [line];
        }
    }
}
