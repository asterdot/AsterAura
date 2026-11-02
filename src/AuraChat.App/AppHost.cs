using System.IO;
using System.Text.Json.Nodes;
using AuraChat.Ai;
using AuraChat.Core;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Chat;
using AuraChat.Features.Life;
using AuraChat.Features.Media;
using AuraChat.Server;

namespace AuraChat.App;

/// <summary>
/// 应用装配：把存储、配置、认证、日志、SSE、人设和 HTTP 服务接起来。
/// 独立版的一切入口都从这里开始（WPF 界面和后台服务共用同一个实例）。
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(
        DataStore store,
        AppLog log,
        ConfigStore config,
        AuthService auth,
        SseHub sse,
        PersonaService personas,
        ApiContext api,
        ServerHost server,
        ChatHub chat,
        SelfDriveService selfDrive)
    {
        Store = store;
        Log = log;
        Config = config;
        Auth = auth;
        Sse = sse;
        Personas = personas;
        Api = api;
        Server = server;
        _chat = chat;
        _selfDrive = selfDrive;
        _tickLoop = TickLoopAsync(_tickCts.Token);
    }

    private readonly ChatHub _chat;
    private readonly SelfDriveService _selfDrive;
    private readonly CancellationTokenSource _tickCts = new();
    private readonly Task _tickLoop;
    private bool _servicesStarted;

    public DataStore Store { get; }
    public AppLog Log { get; }
    public ConfigStore Config { get; }
    public AuthService Auth { get; }
    public SseHub Sse { get; }
    public PersonaService Personas { get; }
    public ApiContext Api { get; }
    public ServerHost Server { get; }

    public static AppHost Create()
    {
        var baseDir = AppContext.BaseDirectory;
        var publicRoot = Path.Combine(baseDir, "public");
        var personasRoot = Path.Combine(baseDir, "personas");

        var store = DataStore.CreateDefault();
        var log = new AppLog();
        log.AttachFile(Path.Combine(store.Root, "logs", "aurachat.log"));

        var config = new ConfigStore(store, log).Load();
        var maxMb = JsonUtil.Num(config.Current["logs"]?["maxMB"], 2);
        log.SetMaxBytes(() => (long)(Math.Max(0.5, maxMb) * 1024 * 1024));

        var auth = new AuthService(store, log);
        var sse = new SseHub(log);
        var personas = new PersonaService(store, personasRoot);
        personas.Seed();

        var env = new FeatureHost
        {
            Store = store,
            Config = config,
            Auth = auth,
            Log = log,
            Persona = personas,
            Emitter = sse,
        };
        env.Set(personas);

        // 功能装配顺序有讲究：工具注册表先建（Life/Media 的工具执行函数注册时要从 env 取 IToolRegistry），
        // 然后 AI 层 → Life（记忆/心情/心事/技能）→ Media（表情/画画/日记/自驱/冲浪/语音）→ 聊天历史 → 聊天中枢。
        AiModule.Create(env);
        env.Set(new ToolRegistry(env));
        LifeModule.Create(env);
        var media = MediaModule.Create(env);
        env.Set(new HistoryStore(env, media));
        var chat = new ChatHub(env);
        env.Set(chat);
        var selfDrive = env.Get<SelfDriveService>()
            ?? throw new InvalidOperationException("自驱服务没装配上");

        var api = new ApiContext
        {
            Env = env,
            Store = store,
            Config = config,
            Auth = auth,
            Log = log,
            Sse = sse,
            PublicRoot = publicRoot,
            PersonasRoot = personasRoot,
        };
        api.Set(personas);

        var server = new ServerHost(api, log);
        api.Host = server;

        var host = new AppHost(store, log, config, auth, sse, personas, api, server, chat, selfDrive);
        log.Child("app").Info($"AuraChat {AuraChatInfo.Version} 装配完成，数据目录 {store.DataDir}");
        return host;
    }

    /// <summary>按配置启动 HTTP 服务。返回失败原因（端口占用之类）。</summary>
    public async Task<string?> StartServerAsync()
    {
        var webHost = JsonUtil.Str(Config.Current["web"]?["host"], "0.0.0.0");
        var port = JsonUtil.Int(Config.Current["web"]?["port"], 2332);
        var (ok, reason) = await Server.StartAsync(webHost, port);
        if (!ok) return reason;
        Sse.StartHeartbeat(TimeSpan.FromSeconds(25));
        Log.Child("app").Info($"HTTP 服务已在 {Server.BoundHost}:{Server.BoundPort} 监听");
        return null;
    }

    /// <summary>给人看的地址（0.0.0.0 时换成 127.0.0.1）。</summary>
    public string LocalUrl
    {
        get
        {
            var host = Server.BoundHost is "0.0.0.0" or "::" or null ? "127.0.0.1" : Server.BoundHost;
            return $"http://{host}:{Server.BoundPort}/";
        }
    }

    /// <summary>启动功能服务：聊天中枢 + 自驱。幂等，进程内只生效一次。</summary>
    public async Task StartServicesAsync()
    {
        if (_servicesStarted) return;
        _servicesStarted = true;
        await _chat.StartAsync();
        await _selfDrive.StartAsync();
        Log.Child("app").Info("功能服务已启动（聊天中枢 / 自驱）");
    }

    /// <summary>每分钟巡检：记忆到点整理、日记到点写、空间到点逛。进程全程跑，出错只记日志。</summary>
    private async Task TickLoopAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await LifeModule.TickAsync(Api.Env, ct);
                    await MediaModule.TickAsync(Api.Env, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Child("app").Warn("每分钟巡检出错：", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* 关机，正常 */ }
    }

    public async ValueTask DisposeAsync()
    {
        _tickCts.Cancel();
        try { await _tickLoop; } catch (OperationCanceledException) { }
        try { await _selfDrive.StopAsync(); } catch { /* 关机时尽力而为 */ }
        try { await _chat.StopAsync(); } catch { /* 关机时尽力而为 */ }
        await Server.DisposeAsync();
        await Sse.DisposeAsync();
    }
}
