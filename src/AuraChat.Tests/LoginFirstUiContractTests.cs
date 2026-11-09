using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// 登录优先前端的静态契约（t13 合同 v1.0 / t14 验收第 3 条）：
/// 冷启动恒为登录页并提供注册入口；不再以 needSetup / owner 是否存在路由到 setup 向导；
/// 认证后的分流是「无角色 → 创建角色，有角色 → 主界面」；401 一律清凭据。
/// 这里断言 app.js / index.html 的结构形状——前端没有 JS 运行时测试设施，源码结构契约是最可靠的合成隔离检查。
/// </summary>
public class LoginFirstUiContractTests
{
    /// <summary>从测试输出目录向上找仓库根（有 AuraChat.sln 的那层）。</summary>
    private static string RepoRoot()
    {
        // 优先用显式环境变量（CI / 外部测试宿主指定仓库位置）
        var env = Environment.GetEnvironmentVariable("AURACHAT_REPO_ROOT");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "AuraChat.sln"))) return env;

        // 再从程序集所在目录、当前工作目录各自向上找 AuraChat.sln
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 20 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AuraChat.sln"))) return dir.FullName;
                dir = dir.Parent!;
            }
        }
        throw new InvalidOperationException("找不到仓库根（AuraChat.sln），无法定位 assets/public");
    }

    private static string AppJs() => File.ReadAllText(Path.Combine(RepoRoot(), "assets", "public", "app.js"));
    private static string IndexHtml() => File.ReadAllText(Path.Combine(RepoRoot(), "assets", "public", "index.html"));

    /// <summary>截取 boot 函数体（从 async function boot( 到其后的 let loginMode），并剥掉行注释。</summary>
    private static string BootBody(string src)
    {
        var start = src.IndexOf("async function boot(", StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js 里没有 boot 函数");
        var end = src.IndexOf("let loginMode", start, StringComparison.Ordinal);
        Assert.True(end > start, "boot 函数体边界异常");
        var body = src[start..end];
        // 剥行注释：needSetup 这类词允许出现在说明注释里，断言只看代码
        var lines = body.Split('\n').Select(l =>
        {
            var idx = l.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? l[..idx] : l;
        });
        return string.Join('\n', lines);
    }

    [Fact]
    public void Boot_AlwaysShowsLogin_NeverRoutesToSetup()
    {
        var boot = BootBody(AppJs());

        // 冷启动一律先到登录/注册页（t13：needSetup/owner 存在与否不改变入口）
        Assert.Contains("showGate('login')", boot);

        // boot 里不允许出现 setup 向导路由与 needSetup 分流（向导只允许登录页上的显式链接触发）
        Assert.DoesNotContain("showGate('setup')", boot);
        Assert.DoesNotContain("needSetup", boot);

        // 唯一的免登录进入主界面路径是 silent 重入（设置保存后的页内刷新），
        // 且只认账号会话并重走 v2 角色分流；旧 owner/guest 会话清凭据回登录（t18 MED-003）
        Assert.Contains("if (silent && me) {", boot);
        Assert.Contains("me.role === 'account'", boot);
        Assert.Contains("await routeAfterAccountAuth(); return", boot);
        Assert.Contains("setToken('')", boot);
    }

    [Fact]
    public void LegacyLogin_NeverEntersApp_ClearsCredentials()
    {
        var src = AppJs();

        // doLogin 的 legacy 分支：旧 owner/guest 只作迁移期校验——成功也不进主界面，
        // 立即清凭据回登录（t25：所有登录模式都必须走 account 认证 + 角色分流）
        var start = src.IndexOf("async function doLogin(", StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js 里没有 doLogin 函数");
        var end = src.IndexOf("async function routeAfterAccountAuth", start, StringComparison.Ordinal);
        Assert.True(end > start, "doLogin 函数体边界异常");
        var body = src[start..end];

        Assert.Contains("mode === 'legacy'", body);
        Assert.Contains("showGate('login', '旧版账号仅用于迁移，请使用账号登录或注册')", body);
        Assert.DoesNotContain("enterApp", body);   // legacy 分支内绝无进入主界面路径
    }

    [Fact]
    public void Api401_AlwaysClearsStoredCredentials()
    {
        var src = AppJs();

        // 过期/无效 Bearer：统一清 token（之后由各调用方回登录页）
        Assert.Contains("if (r.status === 401) { setToken(''); }", src);
    }

    [Fact]
    public void CompanionLookupFailure_ClearsCredentials_ReturnsToLogin()
    {
        var src = AppJs();

        // t28：任何无法验证角色状态的失败（401/403/503/网络）都必须清凭据回登录，不留半认证状态
        var start = src.IndexOf("async function routeAfterAccountAuth", StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js 里没有 routeAfterAccountAuth 函数");
        var end = src.IndexOf("function bindRegister(", start, StringComparison.Ordinal);
        Assert.True(end > start, "routeAfterAccountAuth 函数体边界异常");
        var body = src[start..end];

        Assert.Contains("setToken('')", body);
        Assert.Contains("me = null", body);
        Assert.Contains("showGate('login'", body);
    }

    [Fact]
    public void AuthFlow_UsesV2Endpoints_WithRegisterEntryAndCompanionRouting()
    {
        var src = AppJs();

        // 注册/登录走 v2；旧 /api/login 只作为显式标注的兼容入口保留
        Assert.Contains("/api/v2/auth/register", src);
        Assert.Contains("/api/v2/auth/login", src);
        Assert.Contains("/api/login", src);

        // 认证后的角色分流：有 companion → 主界面；没有 → 创建角色页（t13：无角色进创建、有角色进主界面）
        Assert.Contains("/api/v2/companions/current", src);
        Assert.Contains("if (s.ok && s.companion) { await enterApp(); return }", src);
        Assert.Contains("showGate('companion')", src);

        // 建角色带幂等键（重试安全，不产生重复角色）
        Assert.Contains("Idempotency-Key", src);
    }

    [Fact]
    public void Shell_HasRegisterAndCompanionForms_AndLoginFirstGate()
    {
        var html = IndexHtml();

        // 登录页壳：登录表单、注册入口、旧账号兼容入口、旧版初始化显式链接
        Assert.Contains("id=\"gate-form\"", html);
        Assert.Contains("id=\"reg-form\"", html);
        Assert.Contains("id=\"link-toggle\"", html);
        Assert.Contains("id=\"link-legacy\"", html);
        Assert.Contains("id=\"link-wizard\"", html);

        // 角色创建页壳
        Assert.Contains("id=\"comp-form\"", html);
    }

    [Fact]
    public void Boot_EntryPoint_CallAtStartup()
    {
        var src = AppJs();

        // 页面加载即 boot()（冷启动从登录页开始，不是主界面）
        Assert.Contains("boot()", src);
    }
}
