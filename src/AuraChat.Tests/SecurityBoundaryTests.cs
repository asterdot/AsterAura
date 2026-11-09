using System.Net;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;
using AuraChat.Server;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// 第三冲刺回归（合同 s1）：默认监听 loopback、显式局域网绑定保留、
/// 客户端 URL 不含长期 token、凭证只认 header/cookie（query 只是有界兜底）、日志脱敏。
/// </summary>
public class SecurityBoundaryTests
{
    // ---------- 默认监听边界 ----------

    [Fact]
    public void DefaultConfig_BindsLoopback()
    {
        var cfg = ConfigStore.Normalize(null);
        Assert.Equal("127.0.0.1", (string?)cfg["web"]?["host"]);
    }

    [Fact]
    public void MissingOrEmptyHost_FallsBackToLoopback()
    {
        var missing = ConfigStore.Normalize(new JsonObject());
        Assert.Equal("127.0.0.1", (string?)missing["web"]?["host"]);

        var empty = ConfigStore.Normalize(new JsonObject { ["web"] = new JsonObject { ["host"] = "" } });
        Assert.Equal("127.0.0.1", (string?)empty["web"]?["host"]);
    }

    [Fact]
    public void ExplicitLanHost_IsPreserved()
    {
        // 旧数据显式存的 0.0.0.0 / 局域网地址不能被静默改写（迁移策略：显式配置保留 + 启动时可见提醒）
        var all = ConfigStore.Normalize(new JsonObject { ["web"] = new JsonObject { ["host"] = "0.0.0.0" } });
        Assert.Equal("0.0.0.0", (string?)all["web"]?["host"]);

        var lan = ConfigStore.Normalize(new JsonObject { ["web"] = new JsonObject { ["host"] = "192.168.1.10" } });
        Assert.Equal("192.168.1.10", (string?)lan["web"]?["host"]);
    }

    [Theory]
    [InlineData("", "127.0.0.1")]
    [InlineData("  ", "127.0.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("*", "0.0.0.0")]
    [InlineData("::", "::")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    [InlineData("not-an-ip", "127.0.0.1")]   // 解析不了 → loopback，绝不因配置写错悄悄扩大监听面
    public void ParseHost_BindsExplicitlyOrFallsBackToLoopback(string input, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), HttpUtil.ParseHost(input));
    }

    // ---------- 客户端 URL：根路径 /api/*，不含 token，也不含旧版 /aurachat 前缀 ----------

    [Fact]
    public void ClientUrls_CarryNoToken()
    {
        const string baseUrl = "http://127.0.0.1:2332";
        var urls = new[]
        {
            ClientUrls.Media(baseUrl, "m-abc123"),
            ClientUrls.Sticker(baseUrl, "0123456789abcdef0123456789abcdef"),
            ClientUrls.Avatar(baseUrl, 1234567890),
            ClientUrls.Background(baseUrl, 1234567890),
            ClientUrls.Stream(baseUrl),
        };
        Assert.NotEmpty(urls);
        foreach (var url in urls)
        {
            Assert.DoesNotContain("token=", url);
            // 路径协议三端统一：standalone 服务端固定端口只认根路径 /api/*（ServerHost 拒绝 /aurachat 前缀），
            // 桌面 ClientUrls 与 Android/Windows 客户端（base + "/api/..."）必须拼出同一种地址
            Assert.StartsWith("http://127.0.0.1:2332/api/", url);
            Assert.DoesNotContain("/aurachat", url);
        }
    }

    [Fact]
    public void ClientUrls_Of_MatchesPlainClientConcatenation()
    {
        // Android AuraApi.url / Windows Api.Req 都是 base + "/" + path；桌面必须同一拼法
        const string baseUrl = "http://192.168.1.5:2332/";
        Assert.Equal("http://192.168.1.5:2332/api/state", ClientUrls.Of(baseUrl, "api/state"));
        Assert.Equal("http://192.168.1.5:2332/api/stream", ClientUrls.Stream(baseUrl));
    }

    [Fact]
    public void ClientUrls_MediaEscapesMediaId()
    {
        // 转义后的 id 才不会被当成路径分隔（服务端按第一段取 id）
        var url = ClientUrls.Media("http://127.0.0.1:2332/", "../etc x");
        Assert.EndsWith("/api/media/..%2Fetc%20x", url);
    }

    // ---------- 凭证只认 header/cookie；URL query 只是有界兜底 ----------

    [Fact]
    public void SessionTokenOf_PrefersHeaderAndCookieOverQuery()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer hdr-token";
        ctx.Request.QueryString = new QueryString("?token=query-token");
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx);
        Assert.Equal("hdr-token", token);
        Assert.False(fromQuery);
    }

    [Fact]
    public void SessionTokenOf_CookieBeatsLegacyQuery()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = "oc_s=cookie-token";
        ctx.Request.QueryString = new QueryString("?token=query-token");
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx);
        Assert.Equal("cookie-token", token);
        Assert.False(fromQuery);
    }

    [Fact]
    public void SessionTokenOf_QueryOnly_IsFlaggedAsLegacy()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?token=query-token");
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx);
        Assert.Equal("query-token", token);
        Assert.True(fromQuery);
    }

    [Fact]
    public void SessionTokenOf_NoCredentials_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx);
        Assert.Null(token);
        Assert.False(fromQuery);
    }

    // ---------- 日志 / 错误响应不泄漏凭据 ----------

    [Theory]
    [InlineData("GET /aurachat/api/media/x?token=super-secret-token 401", "token=***")]
    [InlineData("Authorization: Bearer super-secret-token-value", "Bearer ***")]
    public void Redact_MasksCredentialsInLogs(string raw, string masked)
    {
        var redacted = Redact.Apply(raw);
        Assert.Contains(masked, redacted);
        Assert.DoesNotContain("super-secret", redacted);
    }

    // ---------- 旧版 ?token= 兼容有明确截止（2026-12-01 UTC 过后一律 401） ----------

    [Fact]
    public void SessionTokenOf_Query_BeforeDeadline_StillAcceptedAsLegacy()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?token=legacy-token");
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx, HttpUtil.QueryTokenCompatUntil);
        Assert.Equal("legacy-token", token);
        Assert.True(fromQuery);
    }

    [Fact]
    public void SessionTokenOf_Query_AfterDeadline_IsRejected()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?token=legacy-token");
        var after = HttpUtil.QueryTokenCompatUntil.AddSeconds(1);
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx, after);
        Assert.Null(token);          // 不再兜底：按无凭证处理，路由层回 401
        Assert.True(fromQuery);      // 但仍标记来自旧通道，日志能提醒迁移
        Assert.True(HttpUtil.QueryTokenExpired(after));
        Assert.False(HttpUtil.QueryTokenExpired(HttpUtil.QueryTokenCompatUntil));
    }

    [Fact]
    public void SessionTokenOf_Header_AfterDeadline_StillWorks()
    {
        // 截止只砍 query 兼容通道，正常 Authorization 不受影响
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer hdr-token";
        ctx.Request.QueryString = new QueryString("?token=legacy-token");
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx, HttpUtil.QueryTokenCompatUntil.AddYears(1));
        Assert.Equal("hdr-token", token);
        Assert.False(fromQuery);
    }

    // ---------- Bearer 只发给可信 BaseUrl 下的媒体端点（根路径 /api/*） ----------

    [Theory]
    [InlineData("http://127.0.0.1:2332", "/api/media/m-1", true)]
    [InlineData("http://127.0.0.1:2332", "/api/sticker/0123456789abcdef0123456789abcdef", true)]
    [InlineData("http://127.0.0.1:2332", "/api/avatar?v=123", true)]
    [InlineData("http://127.0.0.1:2332", "/api/background?v=123", true)]
    [InlineData("http://127.0.0.1:2332", "/api/media/m-1.png?token=steal", false)]   // query 里想夹带 token
    [InlineData("http://127.0.0.1:2332", "/api/state", false)]                       // 非媒体端点
    [InlineData("http://127.0.0.1:2332", "/api/messages?chatId=x", false)]
    [InlineData("http://127.0.0.1:2332", "/aurachat/api/media/m-1", false)]          // 旧版前缀：服务端已拒绝，别把凭证发给 404
    [InlineData("http://evil.example.com:2332", "/api/media/m-1", false)]            // 别的主机
    [InlineData("http://127.0.0.1:9999", "/api/media/m-1", false)]                   // 别的端口
    [InlineData("https://127.0.0.1:2332", "/api/media/m-1", false)]                  // 协议不一致
    [InlineData("http://127.0.0.1:2332", "ftp://127.0.0.1:2332/api/media/m-1", false)]
    public void IsTrustedMediaUrl_OnlyAllowsMediaEndpointsUnderBaseUrl(string baseUrl, string pathOrUrl, bool trusted)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.Ordinal) ? pathOrUrl : "http://127.0.0.1:2332" + pathOrUrl;
        Assert.Equal(trusted, ClientUrls.IsTrustedMediaUrl(baseUrl, url));
    }

    [Theory]
    [InlineData("", true)]                  // 无 query：纯媒体直链放行
    [InlineData("?v=123", true)]            // 合法缓存戳：可选 ? + v= + 纯数字
    [InlineData("?v=0", true)]
    [InlineData("?v=abc", false)]           // v 非数字：拒绝
    [InlineData("?v=", false)]              // v 为空：拒绝
    [InlineData("?v=123&x=1", false)]       // 合法戳之外还想带别的参数：拒绝
    [InlineData("?x=1&v=123", false)]       // 参数顺序不对/夹带：拒绝
    [InlineData("?foo=1", false)]           // 任意 query：拒绝
    [InlineData("?token=steal", false)]     // query 夹带凭证：拒绝
    [InlineData("?", false)]                // 空 ?：拒绝（不是合法的 v= 戳）
    public void IsTrustedMediaUrl_QueryWhitelist_OnlyNumericVCacheBuster(string query, bool trusted)
    {
        var baseUrl = "http://127.0.0.1:2332";
        Assert.Equal(trusted, ClientUrls.IsTrustedMediaUrl(baseUrl, baseUrl + "/api/avatar" + query));
        Assert.Equal(trusted, ClientUrls.IsTrustedMediaUrl(baseUrl, baseUrl + "/api/background" + query));
    }

    [Fact]
    public void IsTrustedMediaUrl_RejectsGarbage()
    {
        Assert.False(ClientUrls.IsTrustedMediaUrl("http://127.0.0.1:2332", "not a url"));
        Assert.False(ClientUrls.IsTrustedMediaUrl("", "http://127.0.0.1:2332/aurachat/api/media/x"));
        Assert.False(ClientUrls.IsTrustedMediaUrl("http://127.0.0.1:2332", ""));
    }

    // ---------- 出站 TLS：默认严格校验，只有显式环境变量才放开 ----------

    [Fact]
    public void TlsPolicy_DefaultRequiresValidCertificates()
    {
        Environment.SetEnvironmentVariable(TlsPolicy.AllowInsecureEnv, null);
        try
        {
            Assert.False(TlsPolicy.AllowInsecure);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TlsPolicy.AllowInsecureEnv, null);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    public void TlsPolicy_OptInOnlyViaExplicitEnv(string value, bool expected)
    {
        Environment.SetEnvironmentVariable(TlsPolicy.AllowInsecureEnv, value);
        try
        {
            Assert.Equal(expected, TlsPolicy.AllowInsecure);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TlsPolicy.AllowInsecureEnv, null);
        }
    }

    // ---------- 浏览器桥接票：短时、一次性、过期作废 ----------

    [Fact]
    public void Session_ExpiryAndLogout_InvalidateCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "aurachat-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auth = new AuthService(new DataStore(root), new AppLog());
            Assert.True(auth.SetupOwner("boss", "password123").Ok);
            var (_, _, acc) = auth.Verify("boss", "password123");
            Assert.NotNull(acc);
            var token = auth.IssueSession(acc!);
            Assert.NotNull(auth.SessionUserOf(token));

            // 过期会话（30 天 TTL）必须失效，且失效时顺手从会话文件里清掉
            Assert.Null(auth.SessionUserOf(token, DateTimeOffset.UtcNow + AuraChatInfo.SessionTtl + TimeSpan.FromMinutes(1)));
            Assert.Null(auth.SessionUserOf(token));
            Assert.Null(auth.SessionUserOf(""));

            // 登出（DropSession）后同一 token 立即失效
            var token2 = auth.IssueSession(acc!);
            Assert.NotNull(auth.SessionUserOf(token2));
            auth.DropSession(token2);
            Assert.Null(auth.SessionUserOf(token2));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void BridgeTicket_SingleUse_Expiry_AndUniqueness()
    {
        var root = Path.Combine(Path.GetTempPath(), "aurachat-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auth = new AuthService(new DataStore(root), new AppLog());
            Assert.True(auth.SetupOwner("boss", "password123").Ok);
            var (_, _, acc) = auth.Verify("boss", "password123");
            Assert.NotNull(acc);
            var session = auth.IssueSession(acc!);

            var t1 = auth.IssueBridgeTicket(session);
            var t2 = auth.IssueBridgeTicket(session);
            Assert.NotEqual(t1, t2);                       // 票不可预测、不重复
            Assert.DoesNotContain(session, t1, StringComparison.Ordinal);   // 票里不嵌会话凭证

            Assert.Null(auth.ConsumeBridgeTicket("no-such-ticket"));
            Assert.Equal(session, auth.ConsumeBridgeTicket(t1));   // 第一次换票成功
            Assert.Null(auth.ConsumeBridgeTicket(t1));             // 一次性：用掉即焚
            Assert.Null(auth.ConsumeBridgeTicket(t2, DateTimeOffset.UtcNow.AddMinutes(2)));   // 过期作废

            // 真会话不受桥接票影响，可以照常登出
            Assert.NotNull(auth.SessionUserOf(session));
            auth.DropSession(session);
            Assert.Null(auth.SessionUserOf(session));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
