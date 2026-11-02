using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Personas;
using AuraChat.Core.Storage;

namespace AuraChat.Core.Runtime;

/// <summary>
/// 功能模块（聊天 / 记忆 / 心情 / 日记 / 自驱 / 冲浪 / 表情包…）共享的运行时环境。
/// Server 层的 ApiContext 只是它的包装（多一个 HTTP 上下文），这样 Features 不用引用 Server。
/// </summary>
public sealed class FeatureHost
{
    private readonly ConcurrentDictionary<Type, object> _services = new();

    public required DataStore Store { get; init; }
    public required ConfigStore Config { get; init; }
    public required AuthService Auth { get; init; }
    public required AppLog Log { get; init; }
    public required PersonaService Persona { get; init; }

    /// <summary>SSE 广播口子。App 装配时把 SseHub 填进来。</summary>
    public ISseEmitter? Emitter { get; set; }

    /// <summary>状态面板里注册的块。</summary>
    public List<IStatusProvider> StatusProviders { get; } = [];

    /// <summary>「测试」按钮能触发的动作。</summary>
    public List<ITestAction> TestActions { get; } = [];

    /// <summary>system prompt 上下文来源（每个模块各自加一段）。</summary>
    public List<IContextLines> ContextProviders { get; } = [];

    public void AddContext(IContextLines provider) => ContextProviders.Add(provider);

    public void AddStatus(IStatusProvider provider) => StatusProviders.Add(provider);

    public void AddTest(ITestAction action) => TestActions.Add(action);

    // ---------- 模块间服务查找 ----------

    public void Set<T>(T service) where T : class => _services[typeof(T)] = service;

    public T? Get<T>() where T : class => _services.TryGetValue(typeof(T), out var v) ? (T)v : null;

    public T Require<T>() where T : class
        => Get<T>() ?? throw new InvalidOperationException($"服务没装配：{typeof(T).Name}");

    // ---------- SSE ----------

    public void Emit(string type, JsonObject payload) => Emitter?.Emit(type, payload);

    public void Emit(string type, object payload) => Emitter?.Emit(type, payload);

    // ---------- 配置捷径 ----------

    public JsonObject Cfg => Config.Current;

    public JsonNode? Node(params string[] path)
    {
        JsonNode? cur = Config.Current;
        foreach (var key in path)
        {
            if (cur is not JsonObject obj) return null;
            cur = obj[key];
        }
        return cur;
    }

    public string Str(params string[] path) => Node(path)?.ToString() ?? "";

    public string Text(string fallback, params string[] path)
    {
        var v = Node(path)?.ToString();
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    public double Num(double fallback, params string[] path) => JsonUtil.Num(Node(path), fallback);

    public int Int(int fallback, params string[] path) => JsonUtil.Int(Node(path), fallback);

    public bool Flag(bool fallback, params string[] path) => JsonUtil.Bool(Node(path), fallback);
}
