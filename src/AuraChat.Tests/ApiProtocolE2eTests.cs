using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Server;
using Microsoft.AspNetCore.Http;
using SessionRoutes = AuraChat.Server.Routes.SessionRoutes;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// API 路径与认证协议回归（合同 t2/protocol-impl；t32 起夹具改用 v2 合成账号会话，不再依赖 legacy setup/login）：
/// 真实起一个 ServerHost（隔离临时目录），验证 standalone 服务端与三客户端的统一协议：
/// 根路径 /api/* 服务、/aurachat 前缀拒绝、桌面 URL 构造与其他客户端同形、
/// Bearer 覆盖普通/媒体/SSE、URL 不出现长期 token、未凭证 401、旧 query token 仍在有界兼容窗口内。
/// </summary>
public class ApiProtocolE2eTests : IClassFixture<ApiProtocolE2eTests.TestServer>
{
    private readonly TestServer _fx;

    public ApiProtocolE2eTests(TestServer fx) => _fx = fx;

    private HttpClient NewClient() => new() { BaseAddress = new Uri(_fx.BaseUrl + "/") };

    /// <summary>幂等注册合成 v2 账号并拿 Bearer：谁先跑到谁注册，后续测试直接用（xUnit 不保证用例顺序）。
    /// t32：旧夹具走 legacy setup/login；setup 现为默认关闭的迁移面，协议回归一律用 account 会话。</summary>
    private async Task<string> EnsureAccountTokenAsync(HttpClient client)
    {
        if (_fx.AccountToken is { Length: > 0 } cached) return cached;

        var reg = await client.PostAsync("api/v2/auth/register",
            new StringContent("""{"username":"protocol-user","password":"password123"}""", Encoding.UTF8, "application/json"));
        Assert.True(reg.IsSuccessStatusCode, "v2 注册应成功: " + await reg.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await reg.Content.ReadAsStringAsync()) as JsonObject;
        var token = body?["token"]?.ToString();
        Assert.False(string.IsNullOrEmpty(token), "注册应发 token");
        Assert.False(string.IsNullOrEmpty(body?["account"]?["accountId"]?.ToString()), "注册应返回 accountId");
        _fx.AccountToken = token!;
        _fx.AccountId = body!["account"]!["accountId"]!.ToString();
        return token!;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    // ---------- 根路径 /api/*：旧前缀必须被拒绝 ----------

    [Fact]
    public async Task Server_ServesApiAtRoot_RejectsLegacyPrefix()
    {
        using var client = NewClient();
        var r = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var node = JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
        Assert.NotNull(node);
        Assert.True(node!["ok"]!.GetValue<bool>());
        // t28/t31：匿名 state 是最小登录壳——me 为 null，needSetup 等初始化/业务字段不向匿名暴露
        Assert.True(node["me"] is null);
        Assert.True(node["needSetup"] is null);
        Assert.NotNull(node["title"]);
        Assert.NotNull(node["siteName"]);
        Assert.NotNull(node["version"]);

        // 固定端口根路径模式：/aurachat/... 一律 404，绝不静默兼容（协议不搞两套）
        var legacyApi = await client.GetAsync("aurachat/api/state");
        Assert.Equal(HttpStatusCode.NotFound, legacyApi.StatusCode);
        var legacyRoot = await client.GetAsync("aurachat/");
        Assert.Equal(HttpStatusCode.NotFound, legacyRoot.StatusCode);
    }

    // ---------- 三客户端 URL 拼法一致：桌面 ClientUrls == Android/Windows 的 base + "/api/..." ----------

    [Fact]
    public void ClientUrls_MatchAndroidAndWindowsConcatenation()
    {
        // Android AuraApi.url: base.trimEnd('/') + "/" + path；Windows Api.Req: Base + "/" + path.TrimStart('/')
        var androidStyle = _fx.BaseUrl.TrimEnd('/') + "/" + "api/state";
        var windowsStyle = _fx.BaseUrl + "/" + "api/state".TrimStart('/');
        var desktop = ClientUrls.Of(_fx.BaseUrl, "api/state");
        Assert.Equal(androidStyle, desktop);
        Assert.Equal(windowsStyle, desktop);
        Assert.DoesNotContain("aurachat", desktop);
    }

    // ---------- Bearer 覆盖普通 API：桌面客户端同款请求在根路径上可用 ----------

    [Fact]
    public async Task Bearer_LoginState_WorksOnRootPath_WithoutTokenInUrl()
    {
        using var client = NewClient();
        var token = await EnsureAccountTokenAsync(client);

        // 桌面 AppApi 的请求形态：ClientUrls.Of(BaseUrl, path) + Authorization Bearer
        var url = ClientUrls.Of(_fx.BaseUrl, "api/state");
        Assert.DoesNotContain("token=", url);
        var r = await client.SendAsync(Bearer(HttpMethod.Get, url, token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var node = JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
        // t32：协议回归跑在账号会话上——me 带角色标签（首个注册账号=主人账号→owner），subject 就是 accountId
        Assert.Equal("owner", node?["me"]?["role"]?.ToString());
        Assert.Equal(_fx.AccountId, node?["me"]?["subject"]?.ToString());
    }

    // ---------- Bearer 覆盖媒体：URL 无 token，未凭证 401，凭证后通过认证关 ----------

    [Fact]
    public async Task Media_Urls_CarryNoToken_UnauthenticatedIs401()
    {
        using var client = NewClient();
        var token = await EnsureAccountTokenAsync(client);

        foreach (var url in new[]
                 {
                     ClientUrls.Media(_fx.BaseUrl, "no-such-media"),
                     ClientUrls.Sticker(_fx.BaseUrl, "0123456789abcdef0123456789abcdef"),
                 })
        {
            Assert.DoesNotContain("token=", url);
            Assert.DoesNotContain("aurachat", url);

            var anon = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

            var authed = await client.SendAsync(Bearer(HttpMethod.Get, url, token));
            Assert.NotEqual(HttpStatusCode.Unauthorized, authed.StatusCode);   // 凭证被接受，进了业务处理
        }

        // 媒体端点单独断言：过了认证关之后，缺失媒体按业务态 404（MediaStore 里没有这个 id）
        var mediaAuthed = await client.SendAsync(
            Bearer(HttpMethod.Get, ClientUrls.Media(_fx.BaseUrl, "no-such-media"), token));
        Assert.Equal(HttpStatusCode.NotFound, mediaAuthed.StatusCode);
    }

    // ---------- 头像 / 背景 / 贴图：匿名一律 401（不再是无凭证公开资源），凭证后过认证关 ----------

    [Fact]
    public async Task AvatarBackgroundSticker_UnauthenticatedIs401_AuthedPassesGate()
    {
        using var client = NewClient();
        var token = await EnsureAccountTokenAsync(client);

        foreach (var url in new[]
                 {
                     ClientUrls.Avatar(_fx.BaseUrl, 1),
                     ClientUrls.Background(_fx.BaseUrl, 1),
                     ClientUrls.Sticker(_fx.BaseUrl, "0123456789abcdef0123456789abcdef"),
                 })
        {
            Assert.DoesNotContain("token=", url);
            Assert.DoesNotContain("aurachat", url);

            var anon = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

            var authed = await client.SendAsync(Bearer(HttpMethod.Get, url, token));
            Assert.NotEqual(HttpStatusCode.Unauthorized, authed.StatusCode);   // 凭证被接受，过了认证关（业务态：头像/背景未设置、贴图不存在）
        }
    }

    // ---------- Bearer 覆盖 SSE：头带凭证 200 event-stream；无凭证 401 ----------

    [Fact]
    public async Task Stream_BearerHeader_Works_QueryTokenStillBoundedCompat()
    {
        using var client = NewClient();
        var token = await EnsureAccountTokenAsync(client);
        var streamUrl = ClientUrls.Stream(_fx.BaseUrl);
        Assert.DoesNotContain("token=", streamUrl);
        Assert.DoesNotContain("aurachat", streamUrl);

        // 无凭证 → 401
        var anon = await client.GetAsync(streamUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Bearer 头 → 200 + text/event-stream
        using var authed = await client.SendAsync(Bearer(HttpMethod.Get, streamUrl, token),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
        Assert.Contains("text/event-stream", authed.Content.Headers.ContentType?.ToString() ?? "");

        // 旧版 ?token= 兼容窗口内（2026-12-01 UTC 前）仍可连；过了窗口一律 401。
        // 两种分支都写成断言，测试在窗口过后依然成立。
        if (!HttpUtil.QueryTokenExpired(DateTimeOffset.UtcNow))
        {
            using var legacy = await client.GetAsync(streamUrl + "?token=" + Uri.EscapeDataString(token),
                HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        }
        else
        {
            var legacy = await client.GetAsync(streamUrl + "?token=" + Uri.EscapeDataString(token));
            Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        }
    }

    // ---------- 未凭证 401：普通 JSON 接口与状态码协议对齐 ----------

    [Fact]
    public async Task JsonApi_Unauthenticated_Returns401()
    {
        using var client = NewClient();
        await EnsureAccountTokenAsync(client);   // t32：注册合成账号即可；这些路由对匿名一律 401（与 NeedSetup 无关）

        foreach (var path in new[] { "api/status", "api/config", "api/messages?chatId=owner" })
        {
            var r = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            var node = JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
            Assert.False(node?["ok"]?.GetValue<bool>() ?? true);
            Assert.Equal("没登录", node?["reason"]?.ToString());
        }
    }

    // ---------- 401/403 语义：未登录 401，已登录非主人 403（JSON 体保持原样） ----------
    // t32：旧授权语义保留为独立单测——owner/guest 会话在服务层直接构造，不经已默认关闭的 HTTP setup 迁移面

    [Fact]
    public async Task RequireUser_Writes401_RequireOwner_Writes403ForLegacyGuest()
    {
        // 访客机制已退役；改用直接构造旧 guest 会话（role=guest）验证 403 语义：
        // guest 会话通过 RequireUserAsync（已登录），但 RequireOwnerAsync 返回 403。
        var api = _fx.Api;

        var anon = new DefaultHttpContext();
        Assert.Null(await api.RequireUserAsync(new ApiCall(anon, api, "api/status")));
        Assert.Equal(401, anon.Response.StatusCode);

        var anonOwner = new DefaultHttpContext();
        Assert.Null(await api.RequireOwnerAsync(new ApiCall(anonOwner, api, "api/status")));
        Assert.Equal(401, anonOwner.Response.StatusCode);

        // 构造旧 guest 会话：直接用 AuthService.IssueSession 签发 role=guest 的 Account，
        // 不依赖已退役的 CreateGuest API
        var guestAccount = new AuraChat.Core.Auth.Account(
            Subject: "guest-e2e",
            Role: "guest",
            Username: "e2e-guest",
            Salt: "",
            Hash: "",
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level: "social",
            Algorithm: "pbkdf2");
        var guestToken = api.Auth.IssueSession(guestAccount);
        var guestCtx = new DefaultHttpContext();
        guestCtx.Request.Headers.Authorization = "Bearer " + guestToken;
        // guest 已登录 → RequireUserAsync 通过
        Assert.NotNull(await api.RequireUserAsync(new ApiCall(guestCtx, api, "api/status")));
        // 但不是主人 → RequireOwnerAsync 返回 403
        var guestCtx2 = new DefaultHttpContext();
        guestCtx2.Request.Headers.Authorization = "Bearer " + guestToken;
        Assert.Null(await api.RequireOwnerAsync(new ApiCall(guestCtx2, api, "api/status")));
        Assert.Equal(403, guestCtx2.Response.StatusCode);
    }

    // ---------- 公共静态资源仍匿名可读（网页壳），API 不在免名单里 ----------

    [Fact]
    public async Task StaticIndex_IsPublic_ApiIsNot()
    {
        using var client = NewClient();
        var index = await client.GetAsync("index.html");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        var state = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);   // state 匿名仍是 200 登录壳（探活）
        var shell = JsonNode.Parse(await state.Content.ReadAsStringAsync()) as JsonObject;
        Assert.NotNull(shell);
        Assert.True(shell!["ok"]!.GetValue<bool>());
        // t31：匿名壳只有 ok/title/siteName/version——不带 me/needSetup/theme 等任何业务或初始化字段
        Assert.True(shell["me"] is null);
        Assert.True(shell["needSetup"] is null);
        Assert.True(shell["theme"] is null);
        Assert.NotNull(shell["title"]);
        Assert.NotNull(shell["siteName"]);
        Assert.NotNull(shell["version"]);
        var guarded = await client.GetAsync("api/config");
        Assert.Equal(HttpStatusCode.Unauthorized, guarded.StatusCode);
    }

    [Fact]
    public async Task UnknownApiRoute_Returns404_AndErrorBody()
    {
        using var client = NewClient();

        // 已下线的访客接口：路由已删除，未知路由必须回 404（而不是 200 + ok:false 业务失败）
        var guests = await client.GetAsync("api/guests");
        Assert.Equal(HttpStatusCode.NotFound, guests.StatusCode);
        var guestsBody = JsonNode.Parse(await guests.Content.ReadAsStringAsync()) as JsonObject;
        Assert.NotNull(guestsBody);
        Assert.False(guestsBody!["ok"]!.GetValue<bool>());
        Assert.Contains("不认识这个接口", guestsBody["reason"]!.GetValue<string>());

        // 任意不存在的接口同样 404，响应体结构保持一致
        var nope = await client.GetAsync("api/__nope__");
        Assert.Equal(HttpStatusCode.NotFound, nope.StatusCode);
        var nopeBody = JsonNode.Parse(await nope.Content.ReadAsStringAsync()) as JsonObject;
        Assert.NotNull(nopeBody);
        Assert.False(nopeBody!["ok"]!.GetValue<bool>());
    }

    /// <summary>进程内 ServerHost 测试夹具：临时目录隔离，绝不触碰真实用户数据目录。</summary>
    public sealed class TestServer : IDisposable
    {
        private readonly string _root;

        public TestServer()
        {
            _root = Path.Combine(Path.GetTempPath(), "aurachat-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var store = new DataStore(_root, "data");
            var log = new AppLog();
            var config = new ConfigStore(store, log).Load();
            var auth = new AuthService(store, log);
            var sse = new SseHub(log);
            var personasBundled = Path.Combine(_root, "personas-bundled");
            Directory.CreateDirectory(personasBundled);
            var personas = new PersonaService(store, personasBundled);

            var env = new FeatureHost
            {
                Store = store, Config = config, Auth = auth, Log = log, Persona = personas, Emitter = sse,
            };

            var www = Path.Combine(_root, "www");
            Directory.CreateDirectory(www);
            File.WriteAllText(Path.Combine(www, "index.html"), "<!doctype html><html><body>ok</body></html>");

            Api = new ApiContext
            {
                Store = store,
                Config = config,
                Auth = auth,
                Log = log,
                Sse = sse,
                PublicRoot = www,
                PersonasRoot = personasBundled,
                Env = env,
            };
            Api.Set(personas);

            Host = new ServerHost(Api, log);
            var lastReason = "未尝试";
            for (var i = 0; i < 5 && string.IsNullOrEmpty(BaseUrl); i++)
            {
                var (ok, reason) = Host.StartAsync("127.0.0.1", FreePort()).GetAwaiter().GetResult();
                if (ok) BaseUrl = $"http://127.0.0.1:{Host.BoundPort}";
                else lastReason = reason ?? "未知";
            }
            if (string.IsNullOrEmpty(BaseUrl))
                throw new InvalidOperationException("测试服务器 5 次都没能监听上: " + lastReason);
        }

        public ServerHost Host { get; }
        public ApiContext Api { get; }
        public string BaseUrl { get; private set; } = "";
        public string? AccountToken { get; set; }   // t32：v2 合成账号会话（协议回归不再用 legacy owner）
        public string? AccountId { get; set; }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try { return ((IPEndPoint)l.LocalEndpoint).Port; }
            finally { l.Stop(); }
        }

        public void Dispose()
        {
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}

/// <summary>
/// t31/t32：/api/setup 迁移门与 /api/state 显式凭证语义（每个用例独立实例，互不污染共享夹具）。
/// 类名以 ApiProtocolE2eTests_ 前缀挂在同一协议过滤组下（TestRunner 按 typeName Contains 过滤）。
/// 覆盖：默认 404 不建主人；显式开关 + loopback + NeedSetup 一次性迁移 + 成功后自动清开关；
/// 远程来源在门判定层被拒（转发头不被信任）；服务级并发建主人只有一胜；
/// state 对未知/过期/外来方案凭证一律 401 不降级，匿名壳照常 200。
/// 全部使用临时合成目录，绝不触碰真实用户数据。
/// </summary>
public class ApiProtocolE2eTests_SetupGate
{
    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static Task<HttpResponseMessage> PostSetupAsync(HttpClient client, string body)
        => client.PostAsync("api/setup", new StringContent(body, Encoding.UTF8, "application/json"));

    // ---------- 迁移门：默认整条关闭 ----------

    [Fact]
    public async Task Setup_DefaultClosed_Anonymous404_NoOwnerCreated()
    {
        Environment.SetEnvironmentVariable(SessionRoutes.LegacySetupSwitch, null);   // 确保默认关闭
        try
        {
            using var fx = new ApiProtocolE2eTests.TestServer();
            using var client = new HttpClient { BaseAddress = new Uri(fx.BaseUrl + "/") };

            Assert.Equal(HttpStatusCode.NotFound,
                (await PostSetupAsync(client, "{}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await PostSetupAsync(client, """{"username":"sneaky","password":"password123"}""")).StatusCode);

            // 404 背后什么都没做：owner 仍不存在，匿名 state 仍是 200 登录壳
            Assert.Null(fx.Api.Auth.Owner);
            var shell = JsonNode.Parse(await client.GetStringAsync("api/state")) as JsonObject;
            Assert.True(shell!["ok"]!.GetValue<bool>());
            Assert.True(shell["me"] is null);
            Assert.True(shell["needSetup"] is null);
        }
        finally { Environment.SetEnvironmentVariable(SessionRoutes.LegacySetupSwitch, null); }
    }

    // ---------- 迁移门：显式开关 + 一次性 + 自动清除 ----------

    [Fact]
    public async Task Setup_ExplicitSwitch_Loopback_OnceThenClosed()
    {
        Environment.SetEnvironmentVariable(SessionRoutes.LegacySetupSwitch, "1");
        try
        {
            using var fx = new ApiProtocolE2eTests.TestServer();
            using var client = new HttpClient { BaseAddress = new Uri(fx.BaseUrl + "/") };

            // 坏 body：缺字段 / 密码太短 → 400，不创建
            Assert.Equal(HttpStatusCode.BadRequest, (await PostSetupAsync(client, "{}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await PostSetupAsync(client, """{"username":"boss","password":"123"}""")).StatusCode);
            Assert.Null(fx.Api.Auth.Owner);

            // 合法迁移：一次性 200，且迁移面不签发任何会话
            var ok = await PostSetupAsync(client, """{"username":"boss","password":"password123"}""");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var okBody = JsonNode.Parse(await ok.Content.ReadAsStringAsync()) as JsonObject;
            Assert.True(okBody!["ok"]!.GetValue<bool>());
            Assert.True(okBody["token"] is null);
            Assert.NotNull(fx.Api.Auth.Owner);

            // 迁移成功后开关被进程内自动清除
            Assert.Null(Environment.GetEnvironmentVariable(SessionRoutes.LegacySetupSwitch));

            // 就算重开开关，一次性约束兜底：已有主人 → 409
            Environment.SetEnvironmentVariable(SessionRoutes.LegacySetupSwitch, "1");
            var again = await PostSetupAsync(client, """{"username":"other","password":"password123"}""");
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            var againBody = JsonNode.Parse(await again.Content.ReadAsStringAsync()) as JsonObject;
            Assert.Equal("已经有主人了", againBody?["reason"]?.ToString());
        }
        finally { Environment.SetEnvironmentVariable(SessionRoutes.LegacySetupSwitch, null); }
    }

    // ---------- 迁移门：回环判定（不信任转发头） ----------

    [Fact]
    public void SetupGate_NonLoopbackSource_Rejected_ForwardHeadersNeverTrusted()
    {
        // 真实套接字打不出非回环来源（服务器只绑 127.0.0.1），门判定在 DefaultHttpContext 上直接验证；
        // 远程来源 → 路由层 403，且伪造 X-Forwarded-For: 127.0.0.1 不改变判定（只看 TCP 对端地址）
        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        remote.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        Assert.False(SessionRoutes.IsLoopbackRequest(remote));

        var remoteV6 = new DefaultHttpContext();
        remoteV6.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::1");
        Assert.False(SessionRoutes.IsLoopbackRequest(remoteV6));

        var loop = new DefaultHttpContext();
        loop.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(SessionRoutes.IsLoopbackRequest(loop));

        var loopV6 = new DefaultHttpContext();
        loopV6.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        Assert.True(SessionRoutes.IsLoopbackRequest(loopV6));

        Assert.False(SessionRoutes.IsLoopbackRequest(new DefaultHttpContext()));   // 未连接（null）不算回环
    }

    // ---------- 迁移门：并发一次性（服务级原子性） ----------

    [Fact]
    public async Task Setup_ServiceLevel_ConcurrentCreators_ExactlyOneWins()
    {
        // 同一临时账本上并发 SetupOwner：只有一个成功，其余全是「已经有主人了」（路由层映射 409）
        var root = Path.Combine(Path.GetTempPath(), "aurachat-setuprace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var auth = new AuthService(new DataStore(root, "data"), new AppLog());
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => auth.SetupOwner("racer", "password123"))));
            Assert.Equal(1, results.Count(r => r.Ok));
            Assert.Equal(7, results.Count(r => r.Reason == "已经有主人了"));
            Assert.NotNull(auth.Owner);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---------- /api/state：显式无效凭证一律 401，不降级成匿名 ----------

    [Fact]
    public async Task State_ExplicitInvalidCredential_401_NeverAnonymousDowngrade()
    {
        using var fx = new ApiProtocolE2eTests.TestServer();
        using var client = new HttpClient { BaseAddress = new Uri(fx.BaseUrl + "/") };

        // 未知 token（过期/被删会话走同一「认不出」路径）：401 JSON，不是匿名 200
        var unknown = await client.SendAsync(Bearer(HttpMethod.Get, "api/state", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var unknownBody = JsonNode.Parse(await unknown.Content.ReadAsStringAsync()) as JsonObject;
        Assert.False(unknownBody!["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(unknownBody["reason"]?.ToString()));

        // 外来 Authorization 方案（Basic）：显式凭证不被认可 → 401，不装没看见
        var basic = new HttpRequestMessage(HttpMethod.Get, "api/state");
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(basic)).StatusCode);

        // 无任何凭证 → 200 最小匿名壳（登录页照常启动）
        var anon = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.OK, anon.StatusCode);
        var shell = JsonNode.Parse(await anon.Content.ReadAsStringAsync()) as JsonObject;
        Assert.True(shell!["ok"]!.GetValue<bool>());
        Assert.True(shell["me"] is null);
        Assert.True(shell["needSetup"] is null);
        Assert.NotNull(shell["title"]);
        Assert.NotNull(shell["siteName"]);
        Assert.NotNull(shell["version"]);
    }

    [Fact]
    public async Task State_ExpiredBearer_401_ValidBearer_FullState()
    {
        using var fx = new ApiProtocolE2eTests.TestServer();
        using var client = new HttpClient { BaseAddress = new Uri(fx.BaseUrl + "/") };

        var reg = await client.PostAsync("api/v2/auth/register",
            new StringContent("""{"username":"state-user","password":"password123"}""", Encoding.UTF8, "application/json"));
        Assert.True(reg.IsSuccessStatusCode, "注册应成功: " + await reg.Content.ReadAsStringAsync());
        var regBody = JsonNode.Parse(await reg.Content.ReadAsStringAsync()) as JsonObject;
        var token = regBody?["token"]?.ToString();
        Assert.False(string.IsNullOrEmpty(token));

        // 直接改临时目录里的 web-sessions.json：把这条会话的 at 推过 TTL（合成数据，允许）
        var dataDir = Path.GetDirectoryName(fx.Api.Store.PathIn("probe"))!;
        var sessionsPath = Path.Combine(dataDir, "web-sessions.json");
        var sessions = JsonNode.Parse(File.ReadAllText(sessionsPath)) as JsonObject;
        Assert.NotNull(sessions?[token]);
        sessions![token!]["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            - (long)AuraChatInfo.SessionTtl.TotalMilliseconds - 60_000;
        File.WriteAllText(sessionsPath, sessions.ToJsonString());

        // 过期会话对 state：401（不降级成匿名壳）
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Bearer(HttpMethod.Get, "api/state", token!))).StatusCode);

        // 重新登录拿新会话 → state 200 完整状态（account 角色 + 业务元数据）
        var relogin = await client.PostAsync("api/v2/auth/login",
            new StringContent("""{"username":"state-user","password":"password123"}""", Encoding.UTF8, "application/json"));
        var newToken = (JsonNode.Parse(await relogin.Content.ReadAsStringAsync()) as JsonObject)?["token"]?.ToString();
        Assert.False(string.IsNullOrEmpty(newToken));

        var fresh = await client.SendAsync(Bearer(HttpMethod.Get, "api/state", newToken!));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var full = JsonNode.Parse(await fresh.Content.ReadAsStringAsync()) as JsonObject;
        // 本用例自建了全新的服务实例，state-user 是首个注册账号 → 主人账号（role=owner）
        Assert.Equal("owner", full?["me"]?["role"]?.ToString());
        Assert.NotNull(full!["theme"]);   // 业务/运行时元数据只在认证态出现
    }
}
