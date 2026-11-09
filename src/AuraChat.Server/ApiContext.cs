using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using Microsoft.AspNetCore.Http;

namespace AuraChat.Server;

/// <summary>
/// 所有模块共享的运行时上下文。功能模块（聊天/记忆/日记…）通过 Set/Get 挂进来，
/// 路由只依赖这里的接口，避免 Server ↔ Features 互相引用具体类型。
/// </summary>
public sealed class ApiContext
{
    public required DataStore Store { get; init; }
    public required ConfigStore Config { get; init; }
    public required AuthService Auth { get; init; }
    public required AppLog Log { get; init; }
    public required SseHub Sse { get; init; }

    /// <summary>网页静态资源目录（exe 旁边的 public/）。</summary>
    public required string PublicRoot { get; init; }

    /// <summary>人设卡目录（exe 旁边的 personas/）。</summary>
    public required string PersonasRoot { get; init; }

    public string Version => AuraChatInfo.Version;

    /// <summary>HTTP 服务本身（改端口时要用）。启动后由 App 填进来。</summary>
    public ServerHost? Host { get; set; }

    /// <summary>功能模块共享的运行时环境（Features 层只认它，不认 ApiContext）。</summary>
    public required FeatureHost Env { get; init; }

    /// <summary>状态面板里注册的块（心情/日记/记忆/自驱…）。</summary>
    public List<IStatusProvider> StatusProviders => Env.StatusProviders;

    /// <summary>「测试」按钮能触发的动作（speakup/diary/selfdrive/social）。</summary>
    public List<ITestAction> TestActions => Env.TestActions;

    public void AddStatus(IStatusProvider provider) => Env.AddStatus(provider);

    public void AddTest(ITestAction action) => Env.AddTest(action);

    /// <summary>推一条 SSE 事件（message / typing / history）。</summary>
    public void Emit(string type, JsonObject payload) => Env.Emit(type, payload);

    public void Set<T>(T service) where T : class => Env.Set(service);

    public T? Get<T>() where T : class => Env.Get<T>();

    public T Require<T>() where T : class => Env.Require<T>();

    // ---------- 会话辅助 ----------

    /// <summary>
    /// 当前会话身份。会话里的 role 是签发时写死的，但主人身份的事实源是 accounts.json
    /// （主人账号 role=owner）；这里按账本实时校正，普通账号会话不会被提升。
    /// </summary>
    public SessionUser? UserOf(HttpContext ctx)
    {
        var me = Auth.SessionUserOf(HttpUtil.TokenOf(ctx));
        if (me is null) return null;
        if (me.Role == "account" && Get<AccountService>()?.Find(me.Subject) is AccountInfo acc && acc.IsOwner)
            return me with { Role = "owner", Level = "full" };
        return me;
    }

    /// <summary>显示名：账号会话用 accounts.json 里的名字，旧 owner/guest 会话用 users.json。</summary>
    public string DisplayNameOf(SessionUser me)
        => Get<AccountService>()?.Find(me.Subject) is AccountInfo acc
            ? (acc.DisplayName ?? acc.Username)
            : Auth.DisplayNameOf(me.Subject);

    /// <summary>
    /// 要求登录；没登录时回 HTTP 401 + {ok:false,reason:'没登录'} 并返回 null。
    /// 状态码与 /api/stream、媒体端点对齐（未凭证一律 401），JSON 体保持原样，老客户端照常解析。
    /// </summary>
    public async Task<SessionUser?> RequireUserAsync(ApiCall call)
    {
        var me = UserOf(call.Http);
        if (me is not null) return me;
        await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"没登录"}""", "application/json; charset=utf-8");
        return null;
    }

    /// <summary>要求主人；未登录 401，已登录但不是主人回 HTTP 403 + {ok:false,reason:'只有主人能这么做'}。</summary>
    public async Task<SessionUser?> RequireOwnerAsync(ApiCall call)
    {
        var me = await RequireUserAsync(call);
        if (me is null) return null;
        if (me.IsOwner) return me;
        await HttpUtil.SendAsync(call.Http, 403, """{"ok":false,"reason":"只有主人能这么做"}""", "application/json; charset=utf-8");
        return null;
    }

    // ---------- 配置辅助 ----------

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

    public double Num(double fallback, params string[] path)
    {
        var n = Node(path);
        if (n is null) return fallback;
        return double.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public bool Flag(bool fallback, params string[] path)
    {
        var n = Node(path);
        var s = n?.ToString();
        return s is "true" or "false" ? s == "true" : fallback;
    }

    /// <summary>当前聊天 id：单个主人 + 多个客人，按 subject 分会话。</summary>
    public static string ChatIdOf(string subject) => subject;
}
