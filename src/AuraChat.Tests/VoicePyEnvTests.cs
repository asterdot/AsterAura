using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Media;
using Xunit;

namespace AuraChat.Tests;

/// <summary>T6 红绿测试：edge-tts 的 Python 解释器必须能从 venv 的 Scripts\python.exe 找到。</summary>
public class VoicePyEnvTests : IDisposable
{
    private readonly string _root;
    private readonly string _pyenv;

    public VoicePyEnvTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-voice-tests-" + Guid.NewGuid().ToString("N"));
        _pyenv = Path.Combine(_root, "pyenv");
        Environment.SetEnvironmentVariable("AURACHAT_PYENV", _pyenv);
    }

    private VoiceService NewVoice()
    {
        var store = new DataStore(Path.Combine(_root, "data"));
        var log = new AppLog();
        var config = new ConfigStore(store, log).Load();
        var host = new FeatureHost
        {
            Store = store,
            Config = config,
            Auth = new AuthService(store, log),
            Log = log,
            Persona = new PersonaService(store, Path.Combine(_root, "personas-bundled")),
        };
        return new VoiceService(host);
    }

    [Fact]
    public void EdgeState_FindsPythonInScriptsSubfolder()
    {
        // 真实 venv 布局：Scripts\python.exe（uv/venv 在 Windows 的默认位置）
        var scripts = Path.Combine(_pyenv, "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "python.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(_pyenv, "Lib", "site-packages", "edge_tts"));

        var st = NewVoice().EdgeState();

        Assert.True((bool?)st["installed"], $"Scripts\\python.exe 存在时应 installed=true，实际 {st.ToJsonString()}");
    }

    [Fact]
    public void EdgeState_MissingPython_NotInstalled()
    {
        Directory.CreateDirectory(_pyenv);

        var st = NewVoice().EdgeState();

        Assert.False((bool?)st["installed"]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AURACHAT_PYENV", null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
