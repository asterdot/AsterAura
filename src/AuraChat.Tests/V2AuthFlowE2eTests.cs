using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Chat;
using AuraChat.Server;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// t14/t13 登录优先流程的 HTTP 黑盒回归（t13 合同 v1.0）：
/// 注册/登录是仅有的匿名业务端点；会话与角色全部要求 Bearer；重复注册显式 409；
/// 角色至多一个 + Idempotency-Key 幂等重放；会话过期一律 401；跨账号隔离（身份只从会话解析）；
/// 旧 owner/guest 凭据不被静默提升进账号面；历史/记忆文件字节保全。
/// 全部使用临时合成目录，绝不触碰真实用户数据。
/// </summary>
public class V2AuthFlowE2eTests : IClassFixture<V2AuthFlowE2eTests.TestServer>
{
    private readonly TestServer _fx;

    public V2AuthFlowE2eTests(TestServer fx) => _fx = fx;

    private HttpClient NewClient() => new() { BaseAddress = new Uri(_fx.BaseUrl + "/") };

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token, JsonObject? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    private static async Task<JsonObject> SendJsonAsync(HttpClient client, HttpRequestMessage req)
    {
        var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text) as JsonObject;
        return new JsonObject { ["__status"] = (int)resp.StatusCode, ["__body"] = node ?? new JsonObject() };
    }

    private static JsonObject BodyOf(JsonObject r) => r["__body"] as JsonObject ?? new JsonObject();
    private static int StatusOf(JsonObject r) => r["__status"]!.GetValue<int>();

    private async Task<JsonObject> RegisterAsync(HttpClient client, string username, string password)
        => await SendJsonAsync(client, new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/register")
        {
            Content = new StringContent($"{{\"username\":\"{username}\",\"password\":\"{password}\"}}", Encoding.UTF8, "application/json"),
        });

    private async Task<JsonObject> LoginAsync(HttpClient client, string username, string password)
        => await SendJsonAsync(client, new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login")
        {
            Content = new StringContent($"{{\"username\":\"{username}\",\"password\":\"{password}\"}}", Encoding.UTF8, "application/json"),
        });

    // ---------- 全流程：注册 → 会话 → needs-companion → 创建 → ready → 幂等/冲突 ----------

    [Fact]
    public async Task Register_Session_Companion_FullFlow()
    {
        using var client = NewClient();

        var reg = await RegisterAsync(client, "  Nova ", "secret1");
        Assert.Equal(200, StatusOf(reg));
        var token = BodyOf(reg)["token"]?.ToString();
        Assert.False(string.IsNullOrEmpty(token));
        var accountId = BodyOf(reg)["account"]?["accountId"]?.ToString();
        Assert.StartsWith("acct-", accountId);
        Assert.Equal("Nova", BodyOf(reg)["account"]?["username"]?.ToString());   // 规范化去首尾空白

        // 会话摘要（Bearer）
        var session = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/auth/session", token!));
        Assert.Equal(200, StatusOf(session));
        Assert.Equal(accountId, BodyOf(session)["account"]?["accountId"]?.ToString());

        // 无角色 → needs-companion
        var current1 = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token!));
        Assert.Equal(200, StatusOf(current1));
        Assert.Equal("needs-companion", BodyOf(current1)["status"]?.ToString());
        Assert.True(BodyOf(current1)["companion"] is null);

        // 创建角色（带幂等键）
        var create = await SendJsonAsync(client, BearerWithKey(HttpMethod.Post, "api/v2/companions", token!,
            new JsonObject { ["name"] = " 小雪 " }, "key-first"));
        Assert.Equal(200, StatusOf(create));
        var companionId = BodyOf(create)["companion"]?["companionId"]?.ToString();
        Assert.StartsWith("comp-", companionId);
        Assert.Equal("小雪", BodyOf(create)["companion"]?["name"]?.ToString());
        Assert.Equal(accountId, BodyOf(create)["companion"]?["accountId"]?.ToString());

        // 有角色 → ready，同一角色
        var current2 = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token!));
        Assert.Equal(200, StatusOf(current2));
        Assert.Equal("ready", BodyOf(current2)["status"]?.ToString());
        Assert.Equal(companionId, BodyOf(current2)["companion"]?["companionId"]?.ToString());

        // 幂等重放：同一 key（哪怕换了名字）→ 同一角色，不重复写入
        var replay = await SendJsonAsync(client, BearerWithKey(HttpMethod.Post, "api/v2/companions", token!,
            new JsonObject { ["name"] = "改名尝试" }, "key-first"));
        Assert.Equal(200, StatusOf(replay));
        Assert.Equal(companionId, BodyOf(replay)["companion"]?["companionId"]?.ToString());
        Assert.Equal("小雪", BodyOf(replay)["companion"]?["name"]?.ToString());   // 返回的是原角色，不是新名字

        // 新 key 再建：显式 409，返回既有角色，不产生第二条
        var again = await SendJsonAsync(client, BearerWithKey(HttpMethod.Post, "api/v2/companions", token!,
            new JsonObject { ["name"] = "第二个" }, "key-second"));
        Assert.Equal(409, StatusOf(again));
        Assert.Equal(companionId, BodyOf(again)["companion"]?["companionId"]?.ToString());
        var after = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token!));
        Assert.Equal("ready", BodyOf(after)["status"]?.ToString());
    }

    /// <summary>Bearer + Idempotency-Key 头。</summary>
    private static HttpRequestMessage BearerWithKey(HttpMethod method, string url, string token, JsonObject body, string key)
    {
        var req = Bearer(method, url, token, body);
        req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    // ---------- 注册唯一性：规范化重复 → 409，响应里绝不带 token ----------

    [Fact]
    public async Task Register_DuplicateNormalizedUsername_409_NoTokenLeak()
    {
        using var client = NewClient();
        var first = await RegisterAsync(client, "Alice", "secret1");
        Assert.Equal(200, StatusOf(first));

        foreach (var dup in new[] { "Alice", "  alice  ", "ALICE" })
        {
            var r = await RegisterAsync(client, dup, "other22");
            Assert.Equal(409, StatusOf(r));
            Assert.False(BodyOf(r)["ok"]?.GetValue<bool>() ?? true);
            Assert.False(string.IsNullOrEmpty(BodyOf(r)["reason"]?.ToString()));
            Assert.True(BodyOf(r)["token"] is null);   // 冲突响应不发会话
        }
    }

    // ---------- 登录失败：统一提示，不泄漏「用户不存在 / 口令错误」 ----------

    [Fact]
    public async Task Login_WrongCredentials_Uniform401()
    {
        using var client = NewClient();
        var reg = await RegisterAsync(client, "Bella", "secret1");
        Assert.Equal(200, StatusOf(reg));

        var badPassword = await LoginAsync(client, "Bella", "wrong!");
        var noUser = await LoginAsync(client, "nobody-here", "whatever00");

        Assert.Equal(401, StatusOf(badPassword));
        Assert.Equal(401, StatusOf(noUser));
        Assert.Equal(
            BodyOf(badPassword)["reason"]?.ToString(),
            BodyOf(noUser)["reason"]?.ToString());   // 同一提示，防账号枚举
        Assert.True(BodyOf(badPassword)["token"] is null);
        Assert.True(BodyOf(noUser)["token"] is null);
    }

    // ---------- 匿名面：v2 业务端点一律 401 ----------

    [Fact]
    public async Task V2_BusinessEndpoints_Anonymous401()
    {
        using var client = NewClient();

        var session = await client.GetAsync("api/v2/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);

        var current = await client.GetAsync("api/v2/companions/current");
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);

        var create = await client.PostAsync("api/v2/companions",
            new StringContent("""{"name":"匿名"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);

        foreach (var resp in new[] { session, current, create })
        {
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonObject;
            Assert.False(node?["ok"]?.GetValue<bool>() ?? true);
            Assert.Equal("没登录", node?["reason"]?.ToString());
        }
    }

    // ---------- 会话过期：at 被推过 TTL 之后，v2 业务端点一律 401 ----------

    [Fact]
    public async Task Session_ExpiredToken_AllV2Endpoints401()
    {
        using var client = NewClient();
        var reg = await RegisterAsync(client, "Cleo", "secret1");
        var token = BodyOf(reg)["token"]?.ToString();
        Assert.False(string.IsNullOrEmpty(token));

        // 直接改临时目录里的 web-sessions.json：把这条会话的 at 推到 TTL 之外（合成数据，允许）
        var dataDir = Path.GetDirectoryName(_fx.Api.Store.PathIn("probe"))!;
        var sessionsPath = Path.Combine(dataDir, "web-sessions.json");
        var sessions = JsonNode.Parse(File.ReadAllText(sessionsPath)) as JsonObject;
        Assert.NotNull(sessions?[token]);
        var expiredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        - (long)AuraChatInfo.SessionTtl.TotalMilliseconds - 60_000;
        sessions![token!]["at"] = expiredAt;
        File.WriteAllText(sessionsPath, sessions.ToJsonString());

        var session = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/auth/session", token!));
        Assert.Equal(401, StatusOf(session));

        var current = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token!));
        Assert.Equal(401, StatusOf(current));

        // 过期后原凭据重新登录仍可用（账号没被删）
        var relogin = await LoginAsync(client, "Cleo", "secret1");
        Assert.Equal(200, StatusOf(relogin));
        Assert.False(string.IsNullOrEmpty(BodyOf(relogin)["token"]?.ToString()));
    }

    // ---------- 跨账号隔离：身份只从会话解析，body 里的 accountId 一律不信任 ----------

    [Fact]
    public async Task CrossAccount_Isolation_ChatAndCompanion()
    {
        using var client = NewClient();
        var a = await RegisterAsync(client, "Anna", "secret1");
        var b = await RegisterAsync(client, "Ben", "secret1");
        var tokenA = BodyOf(a)["token"]!.ToString();
        var tokenB = BodyOf(b)["token"]!.ToString();
        var idA = BodyOf(a)["account"]!["accountId"]!.ToString();
        var idB = BodyOf(b)["account"]!["accountId"]!.ToString();

        // A 的历史里有一条消息（直接写 HistoryStore，chatId=subject，不经 LLM）
        var history = _fx.Api.Get<IHistoryStore>();
        Assert.NotNull(history);
        history!.Append(idA, new JsonObject { ["who"] = "user", ["uid"] = idA, ["text"] = "Anna的秘密-7f3d" });

        // A 能看到自己的（比较消息 text 字段，不比原始 JSON——ToJsonString 会把中文转义成 \uXXXX）
        var pageA = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/messages", tokenA));
        Assert.Equal(200, StatusOf(pageA));
        var textsA = (BodyOf(pageA)["messages"] as JsonArray ?? new JsonArray())
            .Select(m => m?["text"]?.ToString()).ToList();
        Assert.Contains("Anna的秘密-7f3d", textsA);

        // B 看不到 A 的：chatId 从会话 subject 派生，B 的命名空间是空的
        var pageB = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/messages", tokenB));
        Assert.Equal(200, StatusOf(pageB));
        var textsB = (BodyOf(pageB)["messages"] as JsonArray ?? new JsonArray())
            .Select(m => m?["text"]?.ToString()).ToList();
        Assert.DoesNotContain("Anna的秘密-7f3d", textsB);

        // B 的角色面：A 建了角色，B 的 current 仍是 needs-companion
        var compA = await SendJsonAsync(client, Bearer(HttpMethod.Post, "api/v2/companions", tokenA,
            new JsonObject { ["name"] = "A的角色" }));
        Assert.Equal(200, StatusOf(compA));

        var currentB = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", tokenB));
        Assert.Equal("needs-companion", BodyOf(currentB)["status"]?.ToString());

        // B 试图在 body 里冒用 A 的 accountId：服务端不信任，角色落在 B 自己名下
        var spoof = await SendJsonAsync(client, Bearer(HttpMethod.Post, "api/v2/companions", tokenB,
            new JsonObject { ["accountId"] = idA, ["name"] = "冒名" }));
        Assert.Equal(200, StatusOf(spoof));
        Assert.Equal(idB, BodyOf(spoof)["companion"]?["accountId"]?.ToString());
        Assert.NotEqual(idA, BodyOf(spoof)["companion"]?["accountId"]?.ToString());
    }

    // ---------- 旧身份不提升：owner/guest 会话在 v2 账号面一律被拒 ----------

    [Fact]
    public async Task LegacyOwnerGuest_NotPromotedIntoV2Surfaces()
    {
        using var client = NewClient();
        var ownerToken = await _fx.EnsureOwnerTokenAsync(client);

        // 旧 owner 会话不是账号：会话摘要 401，角色面 404（明确拒绝，不静默映射）
        var session = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/auth/session", ownerToken));
        Assert.Equal(401, StatusOf(session));

        var current = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", ownerToken));
        Assert.Equal(404, StatusOf(current));

        var create = await SendJsonAsync(client, Bearer(HttpMethod.Post, "api/v2/companions", ownerToken,
            new JsonObject { ["name"] = "旧身份抢注" }));
        Assert.Equal(404, StatusOf(create));

        // guest 同理：写一条 guest 记录到 users.json 使其成为 legacy subject（访客机制已退役，
        // 但旧 guest 记录只读兼容，IsLegacySubject 能识别 -> "legacy" -> companions 404 而非 401）
        var usersPath = _fx.Api.Store.PathIn("users.json");
        var usersJson = File.Exists(usersPath)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(usersPath))!.AsObject()
            : new System.Text.Json.Nodes.JsonObject();
        var guestNode = new System.Text.Json.Nodes.JsonObject
        {
            ["subject"] = "guest-v2",
            ["role"] = "guest",
            ["id"] = 1,
            ["username"] = "v2-guest",
            ["salt"] = "",
            ["hash"] = "",
            ["algorithm"] = "pbkdf2",
            ["level"] = "social",
            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (usersJson["guests"] is not System.Text.Json.Nodes.JsonObject existingGuests)
            usersJson["guests"] = new System.Text.Json.Nodes.JsonObject { ["guest-v2"] = guestNode };
        else
            existingGuests["guest-v2"] = guestNode;
        File.WriteAllText(usersPath, usersJson.ToJsonString());
        var guestLegacyAccount = new AuraChat.Core.Auth.Account(
            Subject: "guest-v2",
            Role: "guest",
            Username: "v2-guest",
            Salt: "",
            Hash: "",
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level: "social",
            Algorithm: "pbkdf2");
        var guestToken = _fx.Api.Auth.IssueSession(guestLegacyAccount);
        var guestSession = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/auth/session", guestToken));
        Assert.Equal(401, StatusOf(guestSession));
        var guestCurrent = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", guestToken));
        Assert.Equal(404, StatusOf(guestCurrent));
    }

    // ---------- 历史与记忆文件字节保全：v2 全流程不碰 users.json / 聊天分片 / 记忆 ----------

    [Fact]
    public async Task V2Flows_PreserveLegacyHistoryAndMemoryBytes()
    {
        using var client = NewClient();
        var ownerToken = await _fx.EnsureOwnerTokenAsync(client);

        // 旧体系先有历史：owner 名下一条消息（落分片文件）
        var history = _fx.Api.Get<IHistoryStore>();
        Assert.NotNull(history);
        history!.Append("owner", new JsonObject { ["who"] = "user", ["uid"] = "owner", ["text"] = "主人的历史消息-keep" });

        var dataDir = Path.GetDirectoryName(_fx.Api.Store.PathIn("probe"))!;

        // 旧记忆文件（合成内容，模拟既存 memories.json）
        var memoriesPath = Path.Combine(dataDir, "memories.json");
        if (!File.Exists(memoriesPath))
            File.WriteAllText(memoriesPath, """{"items":[],"note":"synthetic-legacy-memory"}""");

        Dictionary<string, string> Snapshot() => Directory
            .EnumerateFiles(dataDir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(dataDir, f),
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant());

        // v2 会动的文件排除在外（accounts/companions/web-sessions 是新体系自己的状态）
        static bool Volatile(string rel) =>
            rel == "accounts.json" || rel == "companions.json" || rel == "web-sessions.json";

        var before = Snapshot();

        // 跑一遍完整 v2 流程
        var reg = await RegisterAsync(client, "Preserve", "secret1");
        var token = BodyOf(reg)["token"]!.ToString();
        await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/auth/session", token));
        await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token));
        await SendJsonAsync(client, Bearer(HttpMethod.Post, "api/v2/companions", token,
            new JsonObject { ["name"] = "保全" }));
        await LoginAsync(client, "preserve", "secret1");
        await LoginAsync(client, "preserve", "wrong-pw");   // 失败登录也不许碰旧文件

        var after = Snapshot();

        // 键集合一致（无新增/丢失的旧文件），且每个共有文件哈希一致
        var beforeKept = before.Keys.Where(k => !Volatile(k)).ToHashSet();
        var afterKept = after.Keys.Where(k => !Volatile(k)).ToHashSet();
        Assert.Empty(beforeKept.Except(afterKept));
        Assert.Empty(afterKept.Except(beforeKept));
        foreach (var key in beforeKept)
            Assert.True(
                string.Equals(before[key], after[key], StringComparison.Ordinal),
                $"旧文件被改动：{key}");

        // 点名确认：快照里确实有聊天分片（chats/）、记忆文件和 users.json（保全断言不是空转）
        Assert.Contains(beforeKept, k => k.Contains("chats"));
        Assert.Contains("memories.json", beforeKept);
        Assert.Contains("users.json", beforeKept);
    }

    // ---------- t18 MED-004 回归：匿名面收窄（两种 NeedSetup 状态） ----------

    [Fact]
    public async Task AnonymousSurface_NeedSetupTrue_CatalogRequiresBearer_StateLimited()
    {
        // 独立实例：全新临时目录，legacy owner 不存在 → needSetup=true（向导态）
        using var fresh = new TestServer();
        using var client = new HttpClient { BaseAddress = new Uri(fresh.BaseUrl + "/") };

        // t28 收紧：目录/detail 匿名一律 401，NeedSetup=true（向导态）不再豁免
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/personas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/personas/detail?id=none")).StatusCode);

        // /api/state 匿名只暴露登录壳必需字段：无 me、无业务/运行时元数据，
        // 也不含 needSetup（初始化状态存在与否不向匿名泄露）
        var state = JsonNode.Parse(await client.GetStringAsync("api/state")) as JsonObject;
        Assert.True(state!["needSetup"] is null);
        Assert.True(state["me"] is null);
        Assert.True(state["theme"] is null);
        Assert.True(state["botName"] is null);
        Assert.True(state["botTag"] is null);
        Assert.True(state["ownerName"] is null);
        Assert.True(state["persona"] is null);
        Assert.True(state["enabled"] is null);
        Assert.NotNull(state["version"]);
        Assert.NotNull(state["title"]);
        Assert.NotNull(state["siteName"]);

        // 匿名业务面只有注册/登录（v2）：注册即建会话
        var reg = await RegisterAsync(client, "WizardUser", "secret1");
        Assert.Equal(200, StatusOf(reg));
        var token = BodyOf(reg)["token"]!.ToString();
        Assert.False(string.IsNullOrEmpty(token));

        // 登录后（account 会话）目录恢复可达
        Assert.Equal(200, StatusOf(await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/models", token))));
        Assert.Equal(200, StatusOf(await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/personas", token))));
        Assert.Equal(200, StatusOf(await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/personas/detail?id=none", token))));
    }

    [Fact]
    public async Task AnonymousSurface_NeedSetupFalse_CatalogRequiresBearer_StateStaysLimited()
    {
        using var fresh = new TestServer();
        using var client = new HttpClient { BaseAddress = new Uri(fresh.BaseUrl + "/") };

        var ownerToken = await fresh.EnsureOwnerTokenAsync(client);   // 建旧主人（初始化状态不再向匿名泄露）
        var state0 = JsonNode.Parse(await client.GetStringAsync("api/state")) as JsonObject;
        Assert.True(state0!["needSetup"] is null);

        // 已有主人：业务目录/detail 匿名一律 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/models")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/personas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/personas/detail?id=none")).StatusCode);

        // /api/state 匿名仍可达（登录壳字段）但保持有限状态；needSetup 属初始化状态，匿名一律不返回
        var anon = JsonNode.Parse(await client.GetStringAsync("api/state")) as JsonObject;
        Assert.True(anon!["me"] is null);
        Assert.True(anon["theme"] is null);
        Assert.True(anon["botName"] is null);

        // 带 Bearer → 完整状态（me/theme/botName 回来）；目录也恢复可达
        var authed = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/state", ownerToken));
        Assert.Equal(200, StatusOf(authed));
        Assert.Equal("owner", BodyOf(authed)["me"]?["role"]?.ToString());
        Assert.NotNull(BodyOf(authed)["theme"]);
        Assert.NotNull(BodyOf(authed)["botName"]);
        var modelsAuthed = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/models", ownerToken));
        Assert.Equal(200, StatusOf(modelsAuthed));
    }

    // ---------- t18 HIGH-002 回归：孤儿账户会话 / 腐坏账本的显式拒绝 ----------

    [Fact]
    public async Task CompanionRoutes_OrphanAccountSession_Return401()
    {
        using var fresh = new TestServer();
        using var client = new HttpClient { BaseAddress = new Uri(fresh.BaseUrl + "/") };

        var reg = await RegisterAsync(client, "OrphanUser", "secret1");
        Assert.Equal(200, StatusOf(reg));
        var token = BodyOf(reg)["token"]!.ToString();

        // 模拟账户被删（合成数据：accounts.json 移除该账户）；web 会话本身仍有效
        File.WriteAllText(fresh.AccountsFile, """{"accounts":{}}""");

        var current = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token));
        Assert.Equal(401, StatusOf(current));
        Assert.Contains("重新登录", BodyOf(current)["reason"]?.ToString());

        var create = await SendJsonAsync(client, BearerWithKey(HttpMethod.Post, "api/v2/companions", token,
            new JsonObject { ["name"] = "孤儿角色" }, "key-orphan"));
        Assert.Equal(401, StatusOf(create));

        // 拒绝不写任何角色数据
        Assert.False(File.Exists(fresh.CompanionsFile));
    }

    [Fact]
    public async Task CompanionRoutes_CorruptedAccountsStore_Return503_PreserveBytes_AndRecover()
    {
        using var fresh = new TestServer();
        using var client = new HttpClient { BaseAddress = new Uri(fresh.BaseUrl + "/") };

        var reg = await RegisterAsync(client, "CorruptUser", "secret1");
        Assert.Equal(200, StatusOf(reg));
        var token = BodyOf(reg)["token"]!.ToString();
        var goodAccounts = await File.ReadAllTextAsync(fresh.AccountsFile);

        File.WriteAllText(fresh.AccountsFile, "{ 账本坏了");

        // current 与 create 都显式 503（服务端状态可见），绝不谎报 needs-companion
        var current = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token));
        Assert.Equal(503, StatusOf(current));
        Assert.NotNull(BodyOf(current)["reason"]);
        var create = await SendJsonAsync(client, BearerWithKey(HttpMethod.Post, "api/v2/companions", token,
            new JsonObject { ["name"] = "角色" }, "key-c"));
        Assert.Equal(503, StatusOf(create));

        // 坏字节逐字节保留
        Assert.Equal("{ 账本坏了", await File.ReadAllTextAsync(fresh.AccountsFile));

        // 修复后立刻恢复，同一会话继续可用
        await File.WriteAllTextAsync(fresh.AccountsFile, goodAccounts);
        var recovered = await SendJsonAsync(client, Bearer(HttpMethod.Get, "api/v2/companions/current", token));
        Assert.Equal(200, StatusOf(recovered));
        Assert.Equal("needs-companion", BodyOf(recovered)["status"]?.ToString());
    }

    /// <summary>进程内 ServerHost 测试夹具：临时目录隔离 + 真实 HistoryStore，绝不触碰真实用户数据目录。</summary>
    public sealed class TestServer : IDisposable
    {
        private readonly string _root;

        public TestServer()
        {
            _root = Path.Combine(Path.GetTempPath(), "aurachat-v2e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var store = new DataStore(_root, "data");
            AccountsFile = store.PathIn("accounts.json");
            CompanionsFile = store.PathIn("companions.json");
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

            var media = new MediaStore(store, log);
            env.Set(media);
            var historyStore = new HistoryStore(env, media);
            env.Set<IHistoryStore>(historyStore);

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
        public string? OwnerToken { get; set; }
        public string AccountsFile { get; }   // 合成账本路径（孤儿/腐坏用例直接操作合成文件）
        public string CompanionsFile { get; }

        /// <summary>幂等建旧主人 + 旧登录：给「旧身份不提升」与「字节保全」用例提供 owner 会话。</summary>
        public async Task<string> EnsureOwnerTokenAsync(HttpClient client)
        {
            if (OwnerToken is { Length: > 0 } cached) return cached;

            // t32：/api/setup 是默认关闭的迁移面（匿名 404）——旧主人夹具改在服务层直接建，不经 HTTP 迁移门
            var setup = Api.Auth.SetupOwner("boss", "password123");
            Assert.True(setup.Ok || setup.Reason == "已经有主人了", "服务级建主人应成功: " + setup.Reason);
            var login = await client.PostAsync("api/login",
                new StringContent("""{"username":"boss","password":"password123"}""", Encoding.UTF8, "application/json"));
            var body = JsonNode.Parse(await login.Content.ReadAsStringAsync()) as JsonObject;
            var token = body?["token"]?.ToString();
            Assert.False(string.IsNullOrEmpty(token), "旧登录应发 token");
            OwnerToken = token!;
            return token!;
        }

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
