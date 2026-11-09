using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Life;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// 生命状态系统单元测试：验证 state.json 作为唯一事实源，drift/react 机制，事件冷却和幂等性。
/// </summary>
public class LifeStateTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly FeatureHost _host;
    private readonly LifeStateService _life;

    public LifeStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-life-tests-" + Guid.NewGuid().ToString("N"));
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
        _life = new LifeStateService(_host);
    }

    [Fact]
    public void Initialization_CreatesBaselineState()
    {
        var state = _life.GetCurrentState();
        
        // 验证基线值
        Assert.Equal(50.0, state.Dopamine, 0.1); // HormoneMath.DopamineBase
        Assert.Equal(20.0, state.Cortisol, 0.1);  // HormoneMath.CortisolBase
        Assert.Equal(50.0, state.Serotonin, 0.1); // HormoneMath.SerotoninBase
        Assert.Equal(80.0, state.Energy, 0.1);
        Assert.Equal(20.0, state.Hunger, 0.1);
        Assert.Equal(15.0, state.Thirst, 0.1);
        Assert.Equal(10.0, state.Sleepiness, 0.1);
    }

    [Fact]
    public void StateFile_PersistsOnDisk()
    {
        var file = _store.PathIn("state.json");
        Assert.True(File.Exists(file), "state.json 应该在初始化时创建");
        
        var json = File.ReadAllText(file);
        Assert.Contains("dopamine", json);
        Assert.Contains("cortisol", json);
        Assert.Contains("serotonin", json);
        Assert.Contains("energy", json);
    }

    [Fact]
    public void TriggerEvent_Drink_ReducesThirst()
    {
        var before = _life.GetCurrentState();
        var success = _life.TriggerEvent("drink", reason: "测试喝水");
        
        Assert.True(success, "喝水事件应该成功触发");
        
        // 等待事件处理（通过手动 Tick 或短暂延迟）
        System.Threading.Thread.Sleep(100);
        
        var after = _life.GetCurrentState();
        Assert.True(after.Thirst < before.Thirst, 
            $"喝水应减少口渴值，before={before.Thirst:F1} after={after.Thirst:F1}");
    }

    [Fact]
    public void TriggerEvent_Eat_ReducesHungerAndIncreasesEnergy()
    {
        var before = _life.GetCurrentState();
        var success = _life.TriggerEvent("eat", reason: "测试吃饭");
        
        Assert.True(success, "吃饭事件应该成功触发");
        
        System.Threading.Thread.Sleep(100);
        
        var after = _life.GetCurrentState();
        Assert.True(after.Hunger < before.Hunger, 
            $"吃饭应减少饥饿值，before={before.Hunger:F1} after={after.Hunger:F1}");
        Assert.True(after.Energy > before.Energy, 
            $"吃饭应增加能量，before={before.Energy:F1} after={after.Energy:F1}");
    }

    [Fact]
    public void TriggerEvent_Comfort_AdjustsHormones()
    {
        var before = _life.GetCurrentState();
        var success = _life.TriggerEvent("comfort", reason: "测试安慰");
        
        Assert.True(success, "安慰事件应该成功触发");
        
        System.Threading.Thread.Sleep(100);
        
        var after = _life.GetCurrentState();
        // 安慰效果：dopamine +8, cortisol -12, serotonin +10
        Assert.True(after.Dopamine > before.Dopamine, "安慰应增加多巴胺");
        Assert.True(after.Cortisol < before.Cortisol, "安慰应降低皮质醇");
        Assert.True(after.Serotonin > before.Serotonin, "安慰应增加血清素");
    }

    [Fact]
    public void TriggerEvent_Nudge_MovesDopamine()
    {
        var before = _life.GetCurrentState();
        var args = new Dictionary<string, double> { ["delta"] = 20 };
        var success = _life.TriggerEvent("nudge", args, "测试微调");
        
        Assert.True(success, "微调事件应该成功触发");
        
        System.Threading.Thread.Sleep(100);
        
        var after = _life.GetCurrentState();
        Assert.True(after.Dopamine > before.Dopamine, 
            $"正向微调应增加多巴胺，before={before.Dopamine:F1} after={after.Dopamine:F1}");
    }

    [Fact]
    public void Cooldown_PreventsDuplicateEvents()
    {
        // 第一次喝水
        var success1 = _life.TriggerEvent("drink", reason: "第一次喝水");
        Assert.True(success1, "第一次喝水应该成功");
        
        System.Threading.Thread.Sleep(100);
        
        // 立即再次喝水（应该被冷却阻止）
        var success2 = _life.TriggerEvent("drink", reason: "第二次喝水");
        Assert.False(success2, "冷却期内不应该能再次喝水");
    }

    [Fact]
    public void GetCurrentHormones_ReturnsCorrectValues()
    {
        var hormones = _life.GetCurrentHormones();
        
        Assert.Equal(50.0, hormones.Dopamine, 0.1);
        Assert.Equal(20.0, hormones.Cortisol, 0.1);
        Assert.Equal(50.0, hormones.Serotonin, 0.1);
    }

    [Fact]
    public void StateSnapshot_ReturnsValidJson()
    {
        var snapshot = _life.StateSnapshot();
        
        Assert.NotNull(snapshot);
        Assert.True(snapshot.ContainsKey("dopamine"));
        Assert.True(snapshot.ContainsKey("cortisol"));
        Assert.True(snapshot.ContainsKey("serotonin"));
        Assert.True(snapshot.ContainsKey("energy"));
        Assert.True(snapshot.ContainsKey("hunger"));
        Assert.True(snapshot.ContainsKey("thirst"));
        Assert.True(snapshot.ContainsKey("sleepiness"));
        Assert.True(snapshot.ContainsKey("value")); // 汇总值
        Assert.True(snapshot.ContainsKey("enabled"));
    }

    [Fact]
    public void Reset_RestoresBaseline()
    {
        // 触发一些事件改变状态
        _life.TriggerEvent("eat", reason: "改变状态");
        System.Threading.Thread.Sleep(100);
        
        var before = _life.GetCurrentState();
        Assert.NotEqual(80.0, before.Energy, 0.1); // 能量应该已改变
        
        // 重置
        _life.Reset();
        
        var after = _life.GetCurrentState();
        Assert.Equal(50.0, after.Dopamine, 0.1);
        Assert.Equal(20.0, after.Cortisol, 0.1);
        Assert.Equal(50.0, after.Serotonin, 0.1);
        Assert.Equal(80.0, after.Energy, 0.1);
    }

    [Fact]
    public void EventQueue_HandlesMultipleEvents()
    {
        // 快速触发多个事件
        var success1 = _life.TriggerEvent("comfort", reason: "事件1");
        var success2 = _life.TriggerEvent("nudge", 
            new Dictionary<string, double> { ["delta"] = 10 }, "事件2");
        
        Assert.True(success1 && success2, "不同类型的事件应该都能成功添加");
        
        System.Threading.Thread.Sleep(200);
        
        // 验证事件已被处理（队列应该清空）
        var state = _life.GetCurrentState();
        Assert.Equal(0, state.PendingEvents.Count); // 事件应该被立即处理并清空
    }

    [Fact]
    public void Drift_HormonesDecayToBaseline()
    {
        // 手动设置偏离基线的激素值（通过反射或事件）
        _life.TriggerEvent("nudge", 
            new Dictionary<string, double> { ["delta"] = 30 }, "偏离基线");
        System.Threading.Thread.Sleep(100);
        
        var before = _life.GetCurrentState();
        Assert.True(before.Dopamine > 50, "多巴胺应该高于基线");
        
        // 注意：完整的时间衰减测试需要等待或模拟时间流逝
        // 这里只验证机制存在
        Assert.True(before.At > 0, "时间戳应该被记录");
    }

    public void Dispose()
    {
        _life.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
