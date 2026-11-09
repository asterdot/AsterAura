using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Core.Auth;

/// <summary>账号摘要（不含口令材料）。Account 是登录凭证主体；所有账号均为主人（owner）级别，不再区分普通账号。</summary>
public sealed record AccountInfo(string AccountId, string Username, string? DisplayName, long CreatedAt, string Role = "owner")
{
    /// <summary>是否主人账号（主人账号拥有 owner 级权限：日记、记忆、日志等）。</summary>
    public bool IsOwner => Role == "owner";
}

/// <summary>陪伴角色摘要：账号拥有的当前角色实体（首期每账号至多一个）。</summary>
public sealed record CompanionInfo(string CompanionId, string AccountId, string Name, long CreatedAt);

/// <summary>
/// 账号与陪伴角色（t13 合同 v1.0 的本地 MVP）。
/// - Account：accounts.json，登录/注册唯一事实源；旧 owner/guest（users.json）是迁移期兼容，绝不在这里静默认领；
/// - Companion：companions.json，按 accountId 归属；陪伴角色不表示 owner/guest；
/// - 会话沿用 AuthService 的 web-sessions.json：账号会话 Subject=accountId、Role="owner"（所有账号均为主人）。
/// 单机单服务进程：注册/建角色的唯一性由进程内锁 + DataStore 原子写保证（并发时至多一个成功，其余得显式冲突）。
/// </summary>
public sealed class AccountService(DataStore store, AuthService auth, AppLog log)
{
    // 密码策略与 AuthService 保持一致（PBKDF2-SHA256 / 120k / 64B / 16B 盐），不新造算法
    private const int Pbkdf2Iterations = 120_000;
    private const int KeyBytes = 64;
    private const int SaltBytes = 16;

    private readonly string _accountsFile = store.PathIn("accounts.json");
    private readonly string _companionsFile = store.PathIn("companions.json");
    private readonly Lock _gate = new();

    /// <summary>accounts.json 损坏或读不了时的独立诊断；此时拒绝注册/登录，绝不静默重建（否则坏文件会被新数据顶掉）。</summary>
    public string? AccountsIssue { get; private set; }

    /// <summary>companions.json 损坏或读不了时的独立诊断；两个账本互不覆盖——角色加载成功/缺失不得清除账号侧故障（t18 HIGH-001）。</summary>
    public string? CompanionsIssue { get; private set; }

    /// <summary>兼容聚合诊断：任一账本不可用即非空（对外健康总览用）。</summary>
    public string? StoreIssue => AccountsIssue ?? CompanionsIssue;

    // ---------- 账号 ----------

    private JsonObject LoadAccounts()
    {
        var read = DataStore.ReadJsonDetailed(_accountsFile);
        switch (read.Status)
        {
            case JsonReadStatus.Ok:
                AccountsIssue = null;
                var okRoot = read.Node as JsonObject ?? new JsonObject();
                return okRoot;
            case JsonReadStatus.Missing:
                AccountsIssue = null;
                return new JsonObject { ["accounts"] = new JsonObject() };
            default:
                AccountsIssue = read.Status == JsonReadStatus.Corrupted
                    ? "accounts.json 不是合法 JSON，拒绝读写账号；请先恢复或手工移走该文件。"
                    : $"accounts.json 读不了（{read.Error}），拒绝读写账号；请先恢复文件访问。";
                log.Child("account").Error(AccountsIssue!);
                return new JsonObject { ["accounts"] = new JsonObject() };
        }
    }

    private static JsonObject? AccountsOf(JsonObject root) => root["accounts"] as JsonObject;



    /// <summary>
    /// 账号是否是主人账号：新版所有真实存在的账号均为主人（owner），不再区分。
    /// 账本缺失或不可读时按「不是」处理，绝不静默认领。
    /// </summary>
    public bool IsOwnerAccount(string accountId)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null) return false;
            return AccountsOf(root)?[accountId] is JsonObject;
        }
    }

    /// <summary>注册：规范化用户名（去首尾空白、大小写不敏感）唯一；重复返回显式冲突，不静默合并。</summary>
    public (bool Ok, string? Reason, AccountInfo? Account, bool Conflict) Register(string? username, string? password)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null) return (false, AccountsIssue, null, false);
            var name = (username ?? "").Trim();
            if (name.Length == 0 || string.IsNullOrEmpty(password)) return (false, "用户名和密码都不能为空", null, false);
            if (name.Length > 40) return (false, "用户名最多 40 个字符", null, false);
            if (password!.Length < 6) return (false, "密码至少 6 位", null, false);

            var key = name.ToLowerInvariant();
            if (AccountsOf(root) is JsonObject accs)
                foreach (var (_, node) in accs)
                    if (node is JsonObject a && string.Equals(a["usernameKey"]?.ToString(), key, StringComparison.Ordinal))
                        return (false, "用户名已被使用", null, true);

            var accountId = NewId("acct");
            var salt = NewSalt();
            var ledger = AccountsOf(root) ?? new JsonObject();
            // 新版：所有账号都是主人（owner），不再区分第一个和后续账号
            ledger[accountId] = new JsonObject
            {
                ["accountId"] = accountId,
                ["username"] = name,
                ["usernameKey"] = key,
                ["displayName"] = name,
                ["salt"] = salt,
                ["hash"] = HashPassword(password, salt),
                ["algorithm"] = "pbkdf2",
                ["createdAt"] = Now(),
                ["role"] = "owner",
            };
            root["accounts"] = ledger;
            DataStore.WriteJson(_accountsFile, root);
            log.Child("account").Info($"新账号已注册：{name}");
            return (true, null, new AccountInfo(accountId, name, name, Now(), "owner"), false);
        }
    }

    /// <summary>登录：按规范化用户名查找；用户名不存在与口令错误一律同一提示，不泄漏账号枚举细节。</summary>
    public (bool Ok, string? Reason, AccountInfo? Account) Verify(string? username, string? password)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null) return (false, AccountsIssue, null);
            var key = (username ?? "").Trim().ToLowerInvariant();
            JsonObject? hit = null;
            if (AccountsOf(root) is JsonObject accs)
                foreach (var (_, node) in accs)
                    if (node is JsonObject a && string.Equals(a["usernameKey"]?.ToString(), key, StringComparison.Ordinal)) { hit = a; break; }
            if (hit is null) return (false, "用户名或密码不对", null);
            if (!VerifyPassword(password ?? "", hit["salt"]?.ToString() ?? "", hit["hash"]?.ToString() ?? ""))
                return (false, "用户名或密码不对", null);
            return (true, null, InfoOf(root, hit));
        }
    }

    public AccountInfo? Find(string accountId)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null || AccountsOf(root)?[accountId] is not JsonObject a) return null;
            return InfoOf(root, a);
        }
    }

    /// <summary>按用户名查账号（显示名/用户名都试；大小写不敏感）。找不到返回 null。</summary>
    public AccountInfo? FindByUsername(string? username)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null) return null;
            var key = (username ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0) return null;
            if (AccountsOf(root) is not JsonObject accs) return null;
            foreach (var (_, node) in accs)
            {
                if (node is not JsonObject a) continue;
                var uk = a["usernameKey"]?.ToString() ?? "";
                var dn = a["displayName"]?.ToString() ?? "";
                if (uk.Equals(key, StringComparison.OrdinalIgnoreCase) || dn.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return InfoOf(root, a);
            }
            return null;
        }
    }

    public string DisplayNameOf(string accountId)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is null && AccountsOf(root)?[accountId] is JsonObject a)
                return a["displayName"]?.ToString() ?? a["username"]?.ToString() ?? accountId;
            return accountId;
        }
    }

    /// <summary>
    /// 陪伴角色操作前的账户健康门（t18 HIGH-001/002）：
    /// 账户账本必须可读且目标账户必须真实存在——缺失 = 会话指向已不存在的账户（应重新登录），
    /// 账本不健康 = 此刻无法判定（显式拒绝）；两种情况都绝不带着坏账户账本继续发角色。
    /// Status：null=可用，"missing"=账户不存在，"unhealthy"=accounts.json 不可读。
    /// </summary>
    public (bool Ok, string? Reason, string? Status) EnsureAccountUsable(string accountId)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null) return (false, AccountsIssue, "unhealthy");
            if (AccountsOf(root)?[accountId] is not JsonObject) return (false, "账户不存在或已失效，请重新登录", "missing");
            return (true, null, null);
        }
    }

    /// <summary>
    /// 会话身份判定（v2 账号面统一入口）：
    /// - "account"：subject 是账本里真实存在的账号（所有账号均为主人）；
    /// - "legacy"：旧 users.json 的主人/客人会话——不是账号，不进账号面（404）；
    /// - "missing"：账号被删的孤儿会话——需要重新登录（401）；
    /// - "unhealthy"：accounts.json 损坏或读不了——此刻无法判定，显式暴露服务端状态（503）。
    /// </summary>
    public (string Status, string? Reason) ClassifySession(string subject)
    {
        lock (_gate)
        {
            var root = LoadAccounts();
            if (AccountsIssue is not null) return ("unhealthy", AccountsIssue);
            if (AccountsOf(root)?[subject] is JsonObject) return ("account", null);
            if (auth.IsLegacySubject(subject)) return ("legacy", "当前会话没有账号");
            return ("missing", "账户不存在或已失效，请重新登录");
        }
    }

    /// <summary>
    /// 给账号签发会话：沿用 web-sessions.json，Subject=accountId。
    /// 所有账号 Role="owner" / Level="full"（新版无普通账号，账号即主人）。
    /// </summary>
    public string IssueSessionFor(AccountInfo account)
    {
        var legacy = new Account(
            Subject: account.AccountId,
            Role: "owner",
            Username: account.Username,
            Salt: "",
            Hash: "",
            CreatedAt: account.CreatedAt,
            Level: "full",
            Algorithm: "pbkdf2");
        return auth.IssueSession(legacy);
    }

    // ---------- 陪伴角色 ----------

    private JsonObject LoadCompanions()
    {
        var read = DataStore.ReadJsonDetailed(_companionsFile);
        switch (read.Status)
        {
            case JsonReadStatus.Ok:
                CompanionsIssue = null;
                return read.Node as JsonObject ?? FreshCompanions();
            case JsonReadStatus.Missing:
                CompanionsIssue = null;
                return FreshCompanions();
            default:
                CompanionsIssue = read.Status == JsonReadStatus.Corrupted
                    ? "companions.json 不是合法 JSON，拒绝读写陪伴角色；请先恢复或手工移走该文件。"
                    : $"companions.json 读不了（{read.Error}），拒绝读写陪伴角色；请先恢复文件访问。";
                log.Child("account").Error(CompanionsIssue!);
                return FreshCompanions();
        }
    }

    private static JsonObject FreshCompanions() => new() { ["companions"] = new JsonObject(), ["idempotency"] = new JsonObject() };

    /// <summary>
    /// 当前陪伴角色查询（严格语义，t18 HIGH-001/002）：
    /// 只在账户存在且两本账都健康时有意义——账户缺失或任一账本不可读时 Ok=false 显式拒绝，
    /// 绝不把「无法判定」谎报成「无角色」（needs-companion）。
    /// </summary>
    public (bool Ok, string? Reason, CompanionInfo? Companion) CompanionOf(string accountId)
    {
        lock (_gate)
        {
            var usable = EnsureAccountUsable(accountId);
            if (!usable.Ok) return (false, usable.Reason, null);
            var root = LoadCompanions();
            if (CompanionsIssue is not null) return (false, CompanionsIssue, null);
            if (root["companions"] is JsonObject cs)
                foreach (var (_, node) in cs)
                    if (node is JsonObject c && c["accountId"]?.ToString() == accountId)
                        return (true, null, ToCompanion(c));
            return (true, null, null);   // 两本账都健康且确实没有角色 → needs-companion
        }
    }

    /// <summary>
    /// 创建陪伴角色：每账号至多一个（唯一约束，并发重复得显式冲突）；
    /// 支持 Idempotency-Key —— 重放同一成功请求返回同一角色，不重复写入；失败不落半成品，可用同 key 安全重试；
    /// 账户必须真实存在且账本健康（孤儿/伪造会话一律显式拒绝，t18 HIGH-002），腐坏字节绝不覆盖。
    /// </summary>
    public (bool Ok, string? Reason, CompanionInfo? Companion, bool Conflict) CreateCompanion(
        string accountId, string? name, string? idempotencyKey)
    {
        lock (_gate)
        {
            // 账户健康门：账户被删/账本腐坏时在此拒绝，幂等重放也不例外（重放的是角色创建，不是账户存在性豁免）
            var usable = EnsureAccountUsable(accountId);
            if (!usable.Ok) return (false, usable.Reason, null, false);
            var root = LoadCompanions();
            if (CompanionsIssue is not null) return (false, CompanionsIssue, null, false);
            var cs = root["companions"] as JsonObject ?? new JsonObject();
            var idem = root["idempotency"] as JsonObject ?? new JsonObject();

            // 幂等重放：同一账号同一 key → 返回原结果
            if (!string.IsNullOrEmpty(idempotencyKey) && idem[idempotencyKey!] is JsonObject rec)
            {
                var recAccount = rec["accountId"]?.ToString();
                var recCompanion = rec["companionId"]?.ToString();
                if (recAccount == accountId && recCompanion is not null && cs[recCompanion] is JsonObject prev)
                    return (true, null, ToCompanion(prev), false);
                return (false, "幂等键已被使用", null, true);
            }

            // 唯一约束：该账号已有角色 → 显式冲突（返回既有角色供展示，不重复创建）
            foreach (var (_, node) in cs)
                if (node is JsonObject c && c["accountId"]?.ToString() == accountId)
                    return (false, "该账号已经有陪伴角色", ToCompanion(c), true);

            var roleName = (name ?? "").Trim();
            if (roleName.Length == 0) roleName = "AuraChat";
            if (roleName.Length > 40) roleName = roleName[..40];

            var companionId = NewId("comp");
            var companion = new JsonObject
            {
                ["companionId"] = companionId,
                ["accountId"] = accountId,
                ["name"] = roleName,
                ["createdAt"] = Now(),
            };
            cs[companionId] = companion;
            root["companions"] = cs;
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                idem[idempotencyKey!] = new JsonObject { ["accountId"] = accountId, ["companionId"] = companionId };
                root["idempotency"] = idem;
            }
            DataStore.WriteJson(_companionsFile, root);
            log.Child("account").Info($"陪伴角色已创建：{roleName}（{accountId}）");
            return (true, null, ToCompanion(companion), false);
        }
    }

    // ---------- 形状转换 ----------

    /// <summary>
    /// 所有账号均视为主人（owner）：新版账号不再区分 owner/user/account，
    /// 存量 accounts.json 里的旧 role 字段在内存读取时忽略，归一为 "owner"。
    /// </summary>
    private static AccountInfo ToInfo(JsonObject a) => new(
        a["accountId"]?.ToString() ?? "",
        a["username"]?.ToString() ?? "",
        a["displayName"]?.ToString(),
        a["createdAt"]?.GetValue<long>() ?? 0,
        "owner");

    // root 参数保留以兼容调用者签名；新版不再靠 ownerAccountId 决定角色
    private static AccountInfo InfoOf(JsonObject root, JsonObject a) => ToInfo(a);

    private static CompanionInfo ToCompanion(JsonObject c) => new(
        c["companionId"]?.ToString() ?? "",
        c["accountId"]?.ToString() ?? "",
        c["name"]?.ToString() ?? "",
        c["createdAt"]?.GetValue<long>() ?? 0);

    // ---------- 密码/ID 原语（与 AuthService 同参） ----------

    private static string NewId(string prefix) => prefix + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(SaltBytes)).ToLowerInvariant();

    private static string HashPassword(string password, string salt)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(salt),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool VerifyPassword(string password, string salt, string expectedHex)
    {
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(expectedHex)) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); }
        catch { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(salt),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
