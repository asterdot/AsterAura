using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace AuraChat.Server.Routes;

/// <summary>
/// 登录→角色分流 API（t13 合同 v1.0，/api/v2/*）。
/// - 注册/登录是仅有的匿名业务端点（静态壳与健康探测除外）；成功返回 Bearer 会话，凭证不进 URL；
/// - 会话摘要与角色查询/创建全部要求 Bearer；accountId 只从会话解析，绝不信任 body/query 里的身份字段；
/// - 角色至多一个：重复创建/并发竞争返回显式 409；Idempotency-Key 重放同一成功请求返回同一角色；
/// - 旧 /api/login、/api/setup、/api/guests 属迁移期兼容面（SessionRoutes），不在本模块，也不把旧 role 当陪伴角色。
/// </summary>
[RouteModule(9)]
public static class V2AuthRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        var accounts = new AccountService(api.Store, api.Auth, api.Log);
        api.Set(accounts);

        static Task Json(HttpContext ctx, int status, JsonObject body)
            => HttpUtil.SendAsync(ctx, status, body.ToJsonString(), "application/json; charset=utf-8");

        static JsonObject AccountNode(AccountInfo a) => new()
        {
            ["accountId"] = a.AccountId,
            ["username"] = a.Username,
            ["displayName"] = a.DisplayName ?? a.Username,
            ["createdAt"] = a.CreatedAt,
            // 角色只由服务端判定：主人账号 "owner"，普通账号 "user"（前端不再自行假设）
            ["role"] = a.IsOwner ? "owner" : "user",
        };

        static JsonObject CompanionNode(CompanionInfo c) => new()
        {
            ["companionId"] = c.CompanionId,
            ["accountId"] = c.AccountId,
            ["name"] = c.Name,
            ["createdAt"] = c.CreatedAt,
        };

        // ---------- 注册（匿名；成功即建立会话，随后客户端走同一角色分流） ----------
        r.Add("POST", "api/v2/auth/register", async call =>
        {
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason, account, conflict) = accounts.Register(b["username"]?.ToString(), b["password"]?.ToString());
            if (!ok)
            {
                await Json(call.Http, conflict ? 409 : 400, new JsonObject { ["ok"] = false, ["reason"] = reason });
                return;
            }
            var token = accounts.IssueSessionFor(account!);
            await Json(call.Http, 200, new JsonObject { ["ok"] = true, ["token"] = token, ["account"] = AccountNode(account!) });
        });

        // ---------- 登录（匿名；用户名/口令错误一律统一提示） ----------
        r.Add("POST", "api/v2/auth/login", async call =>
        {
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason, account) = accounts.Verify(b["username"]?.ToString(), b["password"]?.ToString());
            if (!ok)
            {
                await Json(call.Http, 401, new JsonObject { ["ok"] = false, ["reason"] = reason ?? "用户名或密码不对" });
                return;
            }
            var token = accounts.IssueSessionFor(account!);
            await Json(call.Http, 200, new JsonObject { ["ok"] = true, ["token"] = token, ["account"] = AccountNode(account!) });
        });

        // ---------- 会话摘要（Bearer） ----------
        r.Add("GET", "api/v2/auth/session", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            // 账号会话的判定只认「会话 subject 是账本里真实存在的账号」——
            // 主人账号的会话 role 是 "owner"，普通账号是 "account"，两者都是账号会话；
            // 旧 users.json 的 owner/guest 会话（subject 不在 accounts.json）一律不算账号会话。
            var kind = accounts.ClassifySession(me.Subject);
            if (kind.Status != "account")
            {
                var status = kind.Status == "unhealthy" ? 503 : 401;
                await Json(call.Http, status, new JsonObject { ["ok"] = false, ["reason"] = kind.Reason ?? "会话无效，请重新登录" });
                return;
            }
            var account = accounts.Find(me.Subject)!;
            await Json(call.Http, 200, new JsonObject { ["ok"] = true, ["account"] = AccountNode(account) });
        });

        // ---------- 当前陪伴角色（Bearer；无记录 = needs-companion，有记录 = ready） ----------
        r.Add("GET", "api/v2/companions/current", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            
            // 分类检查：账号会话 / 旧身份（404）/ 孤儿会话（401）/ 账本不可读（503）
            var kind = accounts.ClassifySession(me.Subject);
            if (kind.Status == "legacy")
            {
                // 旧 owner/guest 会话不是账号：统一 404，不把旧权限身份当陪伴角色
                await Json(call.Http, 404, new JsonObject { ["ok"] = false, ["reason"] = "当前会话没有账号" });
                return;
            }
            if (kind.Status == "unhealthy")
            {
                await Json(call.Http, 503, new JsonObject { ["ok"] = false, ["reason"] = kind.Reason ?? "账号账本不可用" });
                return;
            }
            if (kind.Status == "missing")
            {
                await Json(call.Http, 401, new JsonObject { ["ok"] = false, ["reason"] = kind.Reason ?? "账户不存在或已失效，请重新登录" });
                return;
            }
            
            // t18 HIGH-001/002：会话必须指向真实存在且账本健康的账户——
            // 账户被删/孤儿会话 → 401 要求重新登录；accounts.json 不可读 → 503 显式暴露服务端状态，绝不谎报 needs-companion
            var (usable, why, status) = accounts.EnsureAccountUsable(me.Subject);
            if (!usable)
            {
                await Json(call.Http, status == "missing" ? 401 : 503, new JsonObject { ["ok"] = false, ["reason"] = why });
                return;
            }
            var (companionOk, companionWhy, companion) = accounts.CompanionOf(me.Subject);
            if (!companionOk)
            {
                await Json(call.Http, 503, new JsonObject { ["ok"] = false, ["reason"] = companionWhy });
                return;
            }
            await Json(call.Http, 200, new JsonObject
            {
                ["ok"] = true,
                ["status"] = companion is null ? "needs-companion" : "ready",
                ["companion"] = companion is null ? null : CompanionNode(companion),
            });
        });

        // ---------- 创建陪伴角色（Bearer；至多一个；Idempotency-Key 幂等） ----------
        r.Add("POST", "api/v2/companions", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            
            // 分类检查：账号会话 / 旧身份（404）/ 孤儿会话（401）/ 账本不可读（503）
            var kind = accounts.ClassifySession(me.Subject);
            if (kind.Status == "legacy")
            {
                await Json(call.Http, 404, new JsonObject { ["ok"] = false, ["reason"] = "当前会话没有账号" });
                return;
            }
            if (kind.Status == "unhealthy")
            {
                await Json(call.Http, 503, new JsonObject { ["ok"] = false, ["reason"] = kind.Reason ?? "账号账本不可用" });
                return;
            }
            if (kind.Status == "missing")
            {
                await Json(call.Http, 401, new JsonObject { ["ok"] = false, ["reason"] = kind.Reason ?? "账户不存在或已失效，请重新登录" });
                return;
            }
            
            // t18 HIGH-002：创建前要求账户真实存在且账本健康——孤儿会话/腐坏账本一律显式拒绝
            var (usable, why, status) = accounts.EnsureAccountUsable(me.Subject);
            if (!usable)
            {
                await Json(call.Http, status == "missing" ? 401 : 503, new JsonObject { ["ok"] = false, ["reason"] = why });
                return;
            }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var key = call.Http.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrEmpty(key)) key = null;
            var (ok, reason, companion, conflict) = accounts.CreateCompanion(me.Subject, b["name"]?.ToString(), key);
            if (!ok)
            {
                var body = new JsonObject { ["ok"] = false, ["reason"] = reason };
                if (companion is not null) body["companion"] = CompanionNode(companion);
                await Json(call.Http, conflict ? 409 : 400, body);
                return;
            }
            await Json(call.Http, 200, new JsonObject { ["ok"] = true, ["companion"] = CompanionNode(companion!) });
        });
    }
}
