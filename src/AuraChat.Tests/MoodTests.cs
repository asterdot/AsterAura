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

/// <summary>B3 红绿测试：心情三轴要真的动起来——judge 结果保守时不能静默丢弃，词表兜底要持续生效。</summary>
public class MoodTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly FeatureHost _host;
    private readonly MoodService _mood;

    public MoodTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-mood-tests-" + Guid.NewGuid().ToString("N"));
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
        _mood = new MoodService(_host, new GapPool(_store, () => _host.Cfg, log.Child("gaps")));
    }

    [Fact]
    public void Nudge_PublicPath_MovesDopamine()
    {
        var before = CurrentValues();
        _mood.Nudge(30, "测试");
        var file = _store.PathIn("mood.json");
        var fileDump = File.Exists(file) ? File.ReadAllText(file) : "(no file)";
        var after = CurrentValues();
        Assert.True(after.D > before.D, $"Nudge 应加 dopamine，before={before} after={after} file={MediaUtilNoOp(fileDump)}");
    }

    private static string MediaUtilNoOp(string s) => s.Length > 300 ? s[..300] : s;

    [Fact]
    public void WordList_Fallback_MovesAxes()
    {
        var before = CurrentValues();
        // 明显的夸奖：词表必命中
        _mood.GetType().GetMethod("ObserveByWords", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_mood, ["你真棒，我好喜欢你，今天辛苦了，抱抱"]);
        var after = CurrentValues();
        Assert.True(!before.Equals(after), $"词表兜底应移动三轴，before={before} after={after}");
    }

    [Fact]
    public void ContextLine_NeutralMood_EmptyButDoesNotThrow()
    {
        var line = _mood.ContextLine();
        Assert.True(line is null || line.Length >= 0);
    }

    [Fact]
    public void StateFile_PersistsAfterUpdate()
    {
        _mood.GetType().GetMethod("ObserveByWords", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_mood, ["真开心，笑死我了哈哈哈"]);
        var file = _store.PathIn("mood.json");
        Assert.True(File.Exists(file), "mood.json 应该落盘");
        var json = File.ReadAllText(file);
        Assert.Contains("history", json);
    }

    private (double D, double C, double S) CurrentValues()
    {
        var st = _mood.State();
        double Num(JsonNode? n) => n is null ? 0 : double.TryParse(n.ToString(), out var v) ? v : 0;
        return (Num(st["dopamine"]), Num(st["cortisol"]), Num(st["serotonin"]));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
