using AuraChat.Core;
using AuraChat.Core.Auth;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// t13 合同 v1.0 的账号/陪伴角色 MVP 语义（全部使用临时合成目录，不触碰真实数据）：
/// 注册唯一性 409、登录统一提示、角色至多一个、幂等键重放、坏文件显式拒绝且原字节保留、旧 users.json 不被牵连。
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurachat-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录删不掉就算了 */ }
    }

    private (DataStore Store, AppLog Log, AuthService Auth, AccountService Accounts) NewEnv()
    {
        var store = new DataStore(_root);
        var log = new AppLog();
        var auth = new AuthService(store, log);
        return (store, log, auth, new AccountService(store, auth, log));
    }

    [Fact]
    public void Register_FirstAccount_IsOwner_HasOwnerRoleAndSubject()
    {
        var (_, _, auth, accounts) = NewEnv();
        var (ok, reason, account, conflict) = accounts.Register("  Alice ", "secret1");
        Assert.True(ok, reason);
        Assert.False(conflict);
        Assert.NotNull(account);

        // 主人总是第一个：首个注册的账号就是主人账号，会话 role=owner / level=full
        Assert.Equal("owner", account!.Role);
        Assert.True(accounts.IsOwnerAccount(account!.AccountId));

        var token = accounts.IssueSessionFor(account!);
        var me = auth.SessionUserOf(token);
        Assert.NotNull(me);
        Assert.Equal("owner", me!.Role);
        Assert.Equal(account!.AccountId, me.Subject);
        Assert.Equal("full", me.Level);
    }

    [Fact]
    public void Register_SecondAccount_IsAlsoOwner_HasOwnerRoleAndFullLevel()
    {
        // 新版：所有账号都是主人（owner），不再区分第一个和后续
        var (_, _, auth, accounts) = NewEnv();
        accounts.Register("Alice", "secret1");
        var (ok, reason, account, _) = accounts.Register("Bob", "secret1");
        Assert.True(ok, reason);
        Assert.NotNull(account);

        Assert.Equal("owner", account!.Role);
        Assert.True(accounts.IsOwnerAccount(account!.AccountId));

        var token = accounts.IssueSessionFor(account!);
        var me = auth.SessionUserOf(token);
        Assert.NotNull(me);
        Assert.Equal("owner", me!.Role);
        Assert.Equal(account!.AccountId, me.Subject);
        Assert.Equal("full", me.Level);
    }

    [Fact]
    public void Register_DuplicateNormalizedUsername_IsConflict()
    {
        var (_, _, _, accounts) = NewEnv();
        var first = accounts.Register("Alice", "secret1");
        Assert.True(first.Ok);

        var dupExact = accounts.Register("Alice", "other22");
        Assert.False(dupExact.Ok);
        Assert.True(dupExact.Conflict);

        // 规范化：大小写不敏感 + 首尾空白
        var dupCase = accounts.Register("  alice ", "other22");
        Assert.False(dupCase.Ok);
        Assert.True(dupCase.Conflict);
    }

    [Fact]
    public async Task Register_ConcurrentSameUsername_ExactlyOneWins()
    {
        var (_, _, _, accounts) = NewEnv();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => accounts.Register("racer", "secret1"))));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(5, results.Count(r => !r.Ok && r.Conflict));
    }

    [Fact]
    public void Login_NormalizesUsername_And_WrongCredentialsGetUniformReason()
    {
        var (_, _, _, accounts) = NewEnv();
        accounts.Register("Bob", "secret1");

        var good = accounts.Verify("  bob ", "secret1");
        Assert.True(good.Ok);

        var badPassword = accounts.Verify("Bob", "wrong!");
        Assert.False(badPassword.Ok);
        Assert.Equal("用户名或密码不对", badPassword.Reason);

        var noUser = accounts.Verify("nobody", "whatever");
        Assert.False(noUser.Ok);
        // 不区分「用户名不存在 / 口令错误」，防枚举
        Assert.Equal(badPassword.Reason, noUser.Reason);
    }

    [Fact]
    public void Companion_AtMostOne_PerAccount_And_IdempotencyReplay_ReturnsSame()
    {
        var (_, _, _, accounts) = NewEnv();
        var acc = accounts.Register("Carl", "secret1").Account!;

        var created = accounts.CreateCompanion(acc.AccountId, "小雪 ", "key-1");
        Assert.True(created.Ok);
        Assert.Equal("小雪", created.Companion!.Name);

        // 同 key 重放：同一角色，不重复写入
        var replay = accounts.CreateCompanion(acc.AccountId, "小雪", "key-1");
        Assert.True(replay.Ok);
        Assert.Equal(created.Companion.CompanionId, replay.Companion!.CompanionId);

        // 不同 key 再建：显式冲突，返回既有角色
        var second = accounts.CreateCompanion(acc.AccountId, "另一个", "key-2");
        Assert.False(second.Ok);
        Assert.True(second.Conflict);
        Assert.Equal(created.Companion.CompanionId, second.Companion!.CompanionId);

        // 查询一致（严格语义：健康时 Ok=true）
        var lookup = accounts.CompanionOf(acc.AccountId);
        Assert.True(lookup.Ok);
        Assert.Equal(created.Companion.CompanionId, lookup.Companion!.CompanionId);
    }

    [Fact]
    public void Companion_SameIdempotencyKey_DifferentAccount_IsConflict()
    {
        var (_, _, _, accounts) = NewEnv();
        var a = accounts.Register("Dave", "secret1").Account!;
        var b = accounts.Register("Eve", "secret1").Account!;
        var first = accounts.CreateCompanion(a.AccountId, "A的", "shared-key");
        Assert.True(first.Ok);
        var stolen = accounts.CreateCompanion(b.AccountId, "B的", "shared-key");
        Assert.False(stolen.Ok);
        Assert.True(stolen.Conflict);
    }

    [Fact]
    public void CorruptedAccountsFile_RefusesAndPreservesOriginalBytes()
    {
        var (store, _, auth, accounts) = NewEnv();
        var good = accounts.Register("Frank", "secret1");
        Assert.True(good.Ok);

        var path = store.PathIn("accounts.json");
        var corrupted = "{ 抱歉，这不是 JSON";
        File.WriteAllText(path, corrupted);

        var after = accounts.Register("Grace", "secret1");
        Assert.False(after.Ok);
        Assert.NotNull(accounts.StoreIssue);
        var loginRefused = accounts.Verify("Frank", "secret1");
        Assert.False(loginRefused.Ok);

        // 原坏文件逐字节保留，绝不静默重建
        Assert.Equal(corrupted, File.ReadAllText(path));

        // 修好之后立刻可用（原账号还在）
        File.WriteAllText(path, """{"accounts":{}}""");
        Assert.True(accounts.Verify("Frank", "secret1").Ok is false); // 修复版文件里没有 Frank（原数据在上一个坏文件里，未丢失）
        var reRegister = accounts.Register("Frank", "secret1");
        Assert.True(reRegister.Ok, reRegister.Reason);
        Assert.Null(accounts.StoreIssue);
    }

    [Fact]
    public void LegacyUsersFile_IsUntouchedByAccountOperations()
    {
        var (store, _, auth, accounts) = NewEnv();
        // 旧体系先有主人（users.json）
        var setup = auth.SetupOwner("old-owner", "oldpass1");
        Assert.True(setup.Ok);

        // 新账号体系完全独立
        Assert.True(accounts.Register("Newbie", "secret1").Ok);
        Assert.True(accounts.Verify("Newbie", "secret1").Ok);

        // users.json 的 owner 原样健在，且账号操作没有写它
        Assert.NotNull(auth.Owner);
        Assert.Equal("old-owner", auth.Owner!.Username);
        Assert.False(auth.NeedSetup);
    }

    [Fact]
    public void AccountSession_ExpiresAfterTtl_ThenReloginWorks()
    {
        var (_, _, auth, accounts) = NewEnv();
        var acc = accounts.Register("Henry", "secret1").Account!;
        var token = accounts.IssueSessionFor(acc);

        // TTL 之内有效
        Assert.NotNull(auth.SessionUserOf(token));

        // TTL 之外失效（now 注入，确定性断言 30 天过期语义）
        Assert.Null(auth.SessionUserOf(token, DateTimeOffset.UtcNow + AuraChatInfo.SessionTtl + TimeSpan.FromMinutes(1)));

        // 过期后原凭据重新登录拿新会话，账号没有被过期逻辑误删
        var (ok, _, fresh) = accounts.Verify("Henry", "secret1");
        Assert.True(ok);
        Assert.NotNull(auth.SessionUserOf(accounts.IssueSessionFor(fresh!)));
    }

    // ---------- t18 HIGH-001/002 回归：诊断独立化 + 账户健康门 ----------

    [Fact]
    public void CorruptedAccounts_DiagnosticSurvivesCompanionLoad_AndRefusesCompanionOps()
    {
        var (store, _, _, accounts) = NewEnv();
        Assert.True(accounts.Register("Ivy", "secret1").Ok);

        // accounts.json 腐坏，companions.json 保持健康（缺失）
        File.WriteAllText(store.PathIn("accounts.json"), "{ accounts 坏了");
        File.WriteAllText(store.PathIn("companions.json"), """{"companions":{},"idempotency":{}}""");

        Assert.Equal("unhealthy", accounts.EnsureAccountUsable("acct-whatever").Status);
        Assert.NotNull(accounts.AccountsIssue);
        Assert.Null(accounts.CompanionsIssue);   // 两个账本诊断独立

        // 角色加载成功/缺失不得清除账号侧故障（t18 HIGH-001 的核心场景）
        var probe = accounts.CompanionOf("acct-whatever");
        Assert.False(probe.Ok);
        Assert.NotNull(accounts.AccountsIssue);   // ← 修复前：LoadCompanions 的 Ok 分支会把共享 StoreIssue 清空
        Assert.Null(accounts.CompanionsIssue);

        // 建角色也被账户健康门拒绝，且两个坏/好文件都原样保留
        var create = accounts.CreateCompanion("acct-whatever", "角色", "key-x");
        Assert.False(create.Ok);
        Assert.NotNull(accounts.AccountsIssue);
        Assert.Equal("{ accounts 坏了", File.ReadAllText(store.PathIn("accounts.json")));
        Assert.Equal("""{"companions":{},"idempotency":{}}""", File.ReadAllText(store.PathIn("companions.json")));
    }

    [Fact]
    public void CorruptedCompanions_DiagnosticIndependent_AccountSideStillWorks()
    {
        var (store, _, _, accounts) = NewEnv();
        var acc = accounts.Register("Jack", "secret1").Account!;
        File.WriteAllText(store.PathIn("companions.json"), "{ companions 坏了");

        // 触发一次角色账本加载：CompanionsIssue 独立点亮，AccountsIssue 不被牵连
        var first = accounts.CompanionOf(acc.AccountId);
        Assert.False(first.Ok);
        Assert.Null(accounts.AccountsIssue);
        Assert.NotNull(accounts.CompanionsIssue);

        // 角色账本故障不影响账号注册/登录（诊断独立，互不牵连）
        Assert.True(accounts.Verify("Jack", "secret1").Ok);
        Assert.True(accounts.Register("Kate", "secret1").Ok);

        // 角色查询显式拒绝，绝不把「无法判定」谎报成 needs-companion
        var probe = accounts.CompanionOf(acc.AccountId);
        Assert.False(probe.Ok);
        Assert.NotNull(probe.Reason);

        // 建角色同样拒绝，坏字节原样保留
        var create = accounts.CreateCompanion(acc.AccountId, "角色", "key-y");
        Assert.False(create.Ok);
        Assert.Equal("{ companions 坏了", File.ReadAllText(store.PathIn("companions.json")));

        // 修好之后角色面恢复可用
        File.WriteAllText(store.PathIn("companions.json"), """{"companions":{},"idempotency":{}}""");
        var fixedCreate = accounts.CreateCompanion(acc.AccountId, "角色", "key-z");
        Assert.True(fixedCreate.Ok, fixedCreate.Reason);
        Assert.Null(accounts.CompanionsIssue);
    }

    [Fact]
    public void CompanionOps_RejectMissingAccount_BeforeAnyWrite()
    {
        var (_, _, _, accounts) = NewEnv();
        const string ghost = "acct-000000000000";

        var probe = accounts.CompanionOf(ghost);
        Assert.False(probe.Ok);
        Assert.Equal("missing", accounts.EnsureAccountUsable(ghost).Status);

        var create = accounts.CreateCompanion(ghost, "幽灵角色", "key-ghost");
        Assert.False(create.Ok);
        Assert.False(create.Conflict);   // 不是重名冲突，是账户缺失拒绝
        Assert.Null(create.Companion);

        // 拒绝不落半成品：companions.json 从未被写出
        Assert.False(File.Exists(Path.Combine(_root, "companions.json")));
    }

    [Fact]
    public void OrphanSession_AfterAccountRemoval_CompanionOpsRefuseAndBytesPreserved()
    {
        var (store, _, _, accounts) = NewEnv();
        var acc = accounts.Register("Mallory", "secret1").Account!;
        var created = accounts.CreateCompanion(acc.AccountId, "原有角色", "key-orig");
        Assert.True(created.Ok);
        var companionsPath = store.PathIn("companions.json");
        var before = File.ReadAllText(companionsPath);

        // 模拟账户被删/账本恢复后的孤儿会话：accounts.json 里已没有该账户
        File.WriteAllText(store.PathIn("accounts.json"), """{"accounts":{}}""");

        // 会话仍有效（web-sessions 不知道账户被删），但角色面必须拒绝
        var probe = accounts.CompanionOf(acc.AccountId);
        Assert.False(probe.Ok);
        var create = accounts.CreateCompanion(acc.AccountId, "孤儿角色", "key-orphan");
        Assert.False(create.Ok);
        Assert.Null(create.Companion);

        // companions.json 逐字节未动：不产生孤儿角色，也不覆盖既有数据
        Assert.Equal(before, File.ReadAllText(companionsPath));
    }
    [Fact]
    public void TwoAccounts_BothOwner_BothPassIsOwnerAccount()
    {
        // 新版：多账号并存，互相不降级，全部是主人
        var (_, _, _, accounts) = NewEnv();
        var (_, _, alice, _) = accounts.Register("Alice", "secret1");
        var (_, _, bob, _) = accounts.Register("Bob", "secret2");
        Assert.NotNull(alice);
        Assert.NotNull(bob);
        Assert.Equal("owner", alice!.Role);
        Assert.Equal("owner", bob!.Role);
        Assert.True(accounts.IsOwnerAccount(alice.AccountId));
        Assert.True(accounts.IsOwnerAccount(bob.AccountId));
    }
    [Fact]
    public void LegacyGuestSubject_ClassifiedAsLegacy_NotAccount()
    {
        // 要求 4：旧 guest 会话不得被判成 account
        // ClassifySession("guest-1") -> IsLegacySubject -> "legacy"（不是 "account"）
        var (store, _, auth, accounts) = NewEnv();
        // 向 users.json 写一条旧 guest 记录（只读兼容）
        var usersPath = store.PathIn("users.json");
        File.WriteAllText(usersPath, """
            {"owner":null,"guests":{"guest-1":{"subject":"guest-1","role":"guest","id":1,
            "username":"legacy-guest","salt":"","hash":"","algorithm":"pbkdf2",
            "level":"social","createdAt":0}}}
            """);
        // 没有对应账号 -> 不是 "account"；有 users.json guest 记录 -> "legacy"
        var (status, _) = accounts.ClassifySession("guest-1");
        Assert.Equal("legacy", status);
        Assert.NotEqual("account", status);

        // 新注册的真实账号必须是 "account"
        var acc = accounts.Register("NewUser", "secret1").Account!;
        var (accStatus, _) = accounts.ClassifySession(acc.AccountId);
        Assert.Equal("account", accStatus);
    }
}
