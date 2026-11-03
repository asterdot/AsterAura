using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core.Storage;

namespace AuraChat.Core.Auth;

/// <summary>会话身份（写进 web-sessions.json 的 record 形状）。</summary>
public sealed record SessionUser(string Subject, string Role, string Level, long At)
{
    public bool IsOwner => Role == "owner";
}

/// <summary>账号记录（users.json 里的 owner / guests[subject]）。</summary>
public sealed record Account(
    string Subject,
    string Role,
    string? Username,
    string Salt,
    string Hash,
    long CreatedAt,
    int? Id = null,
    string? DisplayName = null,
    string? Note = null,
    string? Level = null,
    string? Algorithm = null);

/// <summary>
/// 认证与会话。文件格式与字段名逐字对齐原 Node 版 web/auth.js（users.json / web-sessions.json），
/// 迁移时可以直接读旧数据。新密码用 PBKDF2-SHA256；旧版 scrypt 哈希也能直接登录，
/// 验证通过后自动换成 PBKDF2（见 ScryptKdf）。
/// </summary>
public sealed class AuthService(DataStore store, Logging.AppLog log)
{
    private const int Pbkdf2Iterations = 120_000;
    private const int KeyBytes = 64;
    private const int SaltBytes = 16;

    private readonly string _usersFile = store.PathIn("users.json");
    private readonly string _sessionsFile = store.PathIn("web-sessions.json");
    private readonly Lock _gate = new();

    // ---------- 账号文件 ----------

    private JsonObject LoadUsers() => DataStore.ReadObject(_usersFile, new JsonObject { ["owner"] = null, ["guests"] = new JsonObject() });

    private void SaveUsers(JsonObject users) => DataStore.WriteJson(_usersFile, users);

    private JsonObject LoadSessions() => DataStore.ReadObject(_sessionsFile, new JsonObject());

    private void SaveSessions(JsonObject sessions) => DataStore.WriteJson(_sessionsFile, sessions);

    private static JsonObject? GuestsOf(JsonObject users) => users["guests"] as JsonObject;

    private static Account? ToAccount(string subject, JsonObject? node)
    {
        if (node is null) return null;
        string S(string key, string fallback = "") => node[key]?.ToString() ?? fallback;
        return new Account(
            Subject: S("subject", subject),
            Role: S("role", "guest"),
            Username: node["username"]?.ToString(),
            Salt: S("salt"),
            Hash: S("hash"),
            CreatedAt: (long)(node["createdAt"]?.GetValue<double>() ?? 0),
            Id: node["id"] is null ? null : (int)(node["id"]!.GetValue<double>()),
            DisplayName: node["displayName"]?.ToString(),
            Note: node["note"]?.ToString(),
            Level: node["level"]?.ToString(),
            Algorithm: node["algorithm"]?.ToString());
    }

    public Account? Owner
    {
        get
        {
            lock (_gate) return ToAccount("owner", LoadUsers()["owner"] as JsonObject);
        }
    }

    public bool NeedSetup
    {
        get
        {
            lock (_gate) return LoadUsers()["owner"] is null;
        }
    }

    // ---------- 建主人 ----------

    public (bool Ok, string? Reason) SetupOwner(string? username, string? password)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            if (users["owner"] is not null) return (false, "已经有主人了");
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return (false, "用户名和密码都不能为空");
            if (password!.Length < 6) return (false, "密码至少 6 位");

            var salt = NewSalt();
            users["owner"] = new JsonObject
            {
                ["subject"] = "owner",
                ["role"] = "owner",
                ["username"] = username,
                ["salt"] = salt,
                ["hash"] = HashPassword(password, salt),
                ["algorithm"] = "pbkdf2",
                ["createdAt"] = Now(),
            };
            SaveUsers(users);
            log.Child("auth").Info($"主人账号已创建：{username}");
            return (true, null);
        }
    }

    // ---------- 登录 ----------

    public (bool Ok, string? Reason, Account? Account) Verify(string? username, string? password)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            var candidates = new List<Account?>();
            if (users["owner"] is JsonObject owner) candidates.Add(ToAccount("owner", owner));
            if (GuestsOf(users) is JsonObject guests)
                foreach (var (subject, node) in guests) candidates.Add(ToAccount(subject, node as JsonObject));

            var acc = candidates.FirstOrDefault(a => a is not null && a.Username == username);
            if (acc is null) return (false, "用户名或密码不对", null);
            if (!VerifyPassword(password ?? "", acc.Salt, acc.Hash, acc.Algorithm)) return (false, "用户名或密码不对", null);
            // 旧版 Node 用的是 scrypt，登录成功后顺手升级成 PBKDF2，下次就不用再算慢的
            if (!string.Equals(acc.Algorithm, "pbkdf2", StringComparison.OrdinalIgnoreCase))
                UpgradeHash(users, acc, password ?? "");
            return (true, null, acc);
        }
    }

    public string IssueSession(Account account)
    {
        lock (_gate)
        {
            var token = Guid.NewGuid().ToString();
            var sessions = LoadSessions();
            sessions[token] = new JsonObject
            {
                ["subject"] = account.Subject,
                ["role"] = account.Role,
                ["level"] = account.Level ?? (account.Role == "owner" ? "full" : "social"),
                ["at"] = Now(),
            };
            SaveSessions(sessions);
            return token;
        }
    }

    public SessionUser? SessionUserOf(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_gate)
        {
            var sessions = LoadSessions();
            if (sessions[token] is not JsonObject rec) return null;
            var at = (long)(rec["at"]?.GetValue<double>() ?? 0);
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at > (long)AuraChatInfo.SessionTtl.TotalMilliseconds)
            {
                sessions.Remove(token);
                SaveSessions(sessions);
                return null;
            }
            return new SessionUser(
                rec["subject"]?.ToString() ?? "",
                rec["role"]?.ToString() ?? "guest",
                rec["level"]?.ToString() ?? "social",
                at);
        }
    }

    public void DropSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_gate)
        {
            var sessions = LoadSessions();
            sessions.Remove(token);
            SaveSessions(sessions);
        }
    }

    // ---------- 客人 ----------

    public JsonObject? OwnerInfo()
    {
        lock (_gate)
        {
            var owner = ToAccount("owner", LoadUsers()["owner"] as JsonObject);
            return owner is null ? null : new JsonObject
            {
                ["subject"] = "owner",
                ["role"] = "owner",
                ["username"] = owner.Username,
            };
        }
    }

    public List<JsonObject> ListGuests()
    {
        lock (_gate)
        {
            var list = new List<JsonObject>();
            if (GuestsOf(LoadUsers()) is not JsonObject guests) return list;
            foreach (var (subject, node) in guests)
            {
                if (node is not JsonObject g) continue;
                list.Add(new JsonObject
                {
                    ["subject"] = g["subject"]?.ToString() ?? subject,
                    ["role"] = g["role"]?.ToString() ?? "guest",
                    ["id"] = g["id"]?.DeepClone(),
                    ["username"] = g["username"]?.DeepClone(),
                    ["displayName"] = g["displayName"]?.DeepClone(),
                    ["note"] = g["note"]?.DeepClone(),
                    ["level"] = g["level"]?.ToString() ?? "social",
                });
            }
            return list;
        }
    }

    public (bool Ok, string? Reason, string? Subject, int Id, string? Username, string? Password) CreateGuest(
        string? username, string? displayName, string? note, string? level)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            if (users["owner"] is null) return (false, "还没有主人", null, 0, null, null);
            if (string.IsNullOrEmpty(username)) return (false, "用户名不能为空", null, 0, null, null);

            var guests = GuestsOf(users) ?? new JsonObject();
            users["guests"] = guests;

            var ids = guests.Select(kv => kv.Value?["id"] is JsonNode n ? (int)n.GetValue<double>() : 0).ToList();
            var id = ids.Count > 0 ? ids.Max() + 1 : 1;
            var subject = "guest-" + id;
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var salt = NewSalt();

            guests[subject] = new JsonObject
            {
                ["subject"] = subject,
                ["role"] = "guest",
                ["id"] = id,
                ["username"] = username,
                ["displayName"] = string.IsNullOrEmpty(displayName) ? username : displayName,
                ["note"] = note ?? "",
                ["level"] = level == "full" ? "full" : "social",
                ["salt"] = salt,
                ["hash"] = HashPassword(password, salt),
                ["algorithm"] = "pbkdf2",
                ["createdAt"] = Now(),
            };
            SaveUsers(users);
            return (true, null, subject, id, username, password);
        }
    }

    public (bool Ok, string? Reason) UpdateGuest(string subject, JsonObject body)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            if (GuestsOf(users)?[subject] is not JsonObject g) return (false, "没有这个客人");

            if (body.ContainsKey("displayName")) g["displayName"] = body["displayName"]?.ToString();
            if (body.ContainsKey("note")) g["note"] = body["note"]?.ToString();
            if (body.ContainsKey("username") && !string.IsNullOrEmpty(body["username"]?.ToString())) g["username"] = body["username"]!.ToString();
            var level = body["level"]?.ToString();
            if (level is "full" or "social") g["level"] = level;
            if (body["password"] is JsonNode pw && !string.IsNullOrEmpty(pw.ToString()))
            {
                var password = pw.ToString();
                if (password.Length < 6) return (false, "密码至少 6 位");
                var salt = NewSalt();
                g["salt"] = salt;
                g["hash"] = HashPassword(password, salt);
                g["algorithm"] = "pbkdf2";
            }
            SaveUsers(users);
            return (true, null);
        }
    }

    public (bool Ok, string? Reason) ChangePassword(string subject, string? oldPassword, string? newPassword)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            JsonObject? acc = subject == "owner" ? users["owner"] as JsonObject : GuestsOf(users)?[subject] as JsonObject;
            if (acc is null) return (false, "没有这个账号");
            if (string.IsNullOrEmpty(newPassword)) return (false, "新密码不能为空");
            if (newPassword!.Length < 6) return (false, "密码至少 6 位");
            if (!VerifyPassword(oldPassword ?? "", acc["salt"]?.ToString() ?? "", acc["hash"]?.ToString() ?? "")) return (false, "旧密码不对");

            var salt = NewSalt();
            acc["salt"] = salt;
            acc["hash"] = HashPassword(newPassword, salt);
            acc["algorithm"] = "pbkdf2";
            SaveUsers(users);
            return (true, null);
        }
    }

    public (bool Ok, string? Reason) DeleteGuest(string subject)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            if (GuestsOf(users)?[subject] is null) return (false, "没有这个客人");
            (GuestsOf(users)!).Remove(subject);
            SaveUsers(users);

            var sessions = LoadSessions();
            foreach (var (token, rec) in sessions.ToList())
                if (rec?["subject"]?.ToString() == subject) sessions.Remove(token);
            SaveSessions(sessions);
            return (true, null);
        }
    }

    public string DisplayNameOf(string subject)
    {
        lock (_gate)
        {
            var users = LoadUsers();
            if (subject == "owner") return (users["owner"] as JsonObject)?["username"]?.ToString() ?? "主人";
            var g = GuestsOf(users)?[subject] as JsonObject;
            return g?["displayName"]?.ToString() ?? g?["username"]?.ToString() ?? subject;
        }
    }

    // ---------- 密码 ----------

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

    private static bool VerifyPassword(string password, string salt, string expectedHex, string? algorithm = null)
    {
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(expectedHex)) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); }
        catch { return false; }

        // 旧版 Node：crypto.scryptSync(pw, salt, 64)，默认 N=16384 / r=8 / p=1
        if (!string.Equals(algorithm, "pbkdf2", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var legacy = ScryptKdf.Hash(
                    Encoding.UTF8.GetBytes(password),
                    Encoding.UTF8.GetBytes(salt),
                    ScryptKdf.LegacyN, ScryptKdf.LegacyR, ScryptKdf.LegacyP, expected.Length);
                if (CryptographicOperations.FixedTimeEquals(legacy, expected)) return true;
            }
            catch { /* 参数不合法就落到 PBKDF2 再试一次 */ }
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(salt),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>把旧 scrypt 账号悄悄升级成 PBKDF2（密码本身不变，用户无感）。</summary>
    private void UpgradeHash(JsonObject users, Account account, string password)
    {
        JsonNode? node = account.Role == "owner"
            ? users["owner"]
            : GuestsOf(users)?[account.Subject];
        if (node is not JsonObject record) return;
        var salt = NewSalt();
        record["salt"] = salt;
        record["hash"] = HashPassword(password, salt);
        record["algorithm"] = "pbkdf2";
        SaveUsers(users);
        log.Child("auth").Info($"旧密码哈希已升级为 PBKDF2：{account.Subject}");
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
