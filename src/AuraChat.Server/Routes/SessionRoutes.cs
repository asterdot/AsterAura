using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AuraChat.Core.Ai;
using AuraChat.Core.Auth;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using Microsoft.AspNetCore.Http;

[assembly: InternalsVisibleTo("AuraChat.Tests")]

namespace AuraChat.Server.Routes;

/// <summary>登录 / 初始化向导 / 人设 / 客人（对齐原 web/routes/session.js）。</summary>
[RouteModule(10)]
public static class SessionRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        var personas = api.Get<PersonaService>() ?? new PersonaService(api.Store, api.PersonasRoot);

        // ---------- 状态 ----------
        // t18 MED-004 + t28 收紧：未认证只暴露登录壳必需字段（标题/站点名/版本）；
        // needSetup 属旧系统初始化状态，匿名一律不返回（不得据此区分系统状态）；
        // 业务与运行时元数据（me/needSetup/botName/botTag/ownerName/persona/enabled/theme）一律要求 Bearer 会话
        r.Add("GET", "api/state", async call =>
        {
            // t32（t31 合同）：没带凭证 → 200 最小匿名壳；显式带了但无效/过期/malformed（Bearer token 不认识、
            // oc_s cookie 失效、或 Authorization 用了别的方案）一律 401 JSON，绝不降级成匿名 200——
            // 否则客户端分不清「没登录」和「登录已失效」，坏会话会被静默当成登出。
            var authz = call.Http.Request.Headers.Authorization.ToString();
            var presented = HttpUtil.TokenOf(call.Http);
            var me = string.IsNullOrWhiteSpace(presented) ? null : api.Auth.SessionUserOf(presented);
            if (me is null && HasExplicitCredential(authz, presented))
            {
                await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"登录已过期，请重新登录"}""", "application/json; charset=utf-8");
                return;
            }
            var cfg = api.Cfg;
            var result = new JsonObject
            {
                ["ok"] = true,
                ["me"] = me is null ? null : new JsonObject
                {
                    ["subject"] = me.Subject,
                    ["role"] = me.Role,
                    // 账号会话（主人账号 role=owner / 普通账号 role=account）的名字从 accounts.json 解析；
                    // 旧 users.json 的 owner/guest 会话仍走 users.json
                    ["name"] = api.Get<AccountService>()?.Find(me.Subject) is AccountInfo acc
                        ? (acc.DisplayName ?? acc.Username)
                        : api.Auth.DisplayNameOf(me.Subject),
                    ["level"] = string.IsNullOrEmpty(me.Level) ? (me.IsOwner ? "full" : "social") : me.Level,
                },
            };
            if (me is null)
            {
                result["title"] = NonEmpty(api.Str("pageTitle"), "AuraChat");
                result["siteName"] = NonEmpty(api.Str("siteName"), "AuraChat");
                result["version"] = api.Version;
            }
            else
            {
                result["needSetup"] = api.Auth.NeedSetup;
                result["botName"] = NonEmpty(api.Str("botName"), "AuraChat");
                result["botTag"] = api.Str("botTag");
                result["title"] = NonEmpty(api.Str("pageTitle"), "AuraChat");
                result["siteName"] = NonEmpty(api.Str("siteName"), "AuraChat");
                result["version"] = api.Version;
                result["ownerName"] = NonEmpty(api.Str("ownerName"), "主人");
                result["persona"] = api.Str("persona", "current");
                result["enabled"] = api.Flag(true, "enabled");
                result["theme"] = cfg["theme"]?.DeepClone() ?? new JsonObject { ["mode"] = "follow", ["background"] = "" };
            }
            await HttpUtil.JsonAsync(call.Http, result);
        });

        // ---------- 初始化向导：建主人（t31/t32：旧系统迁移兼容面，默认整条关闭） ----------
        // 默认 404（不向探测泄露这条迁移路径的存在）；仅显式 AURACHAT_ALLOW_LEGACY_SETUP=1 且本机回环来源
        // （只看 TCP 对端地址，绝不信任转发头）且尚未有主人时放行。只做一次性迁移，不签发任何会话；
        // 并发竞争在 AuthService 的锁内决出唯一成功，其余 409；成功后进程内自动清除开关，此后回到 404。
        r.Add("POST", "api/setup", async call =>
        {
            if (Environment.GetEnvironmentVariable(LegacySetupSwitch) != "1")
            {
                await HttpUtil.SendAsync(call.Http, 404, "Not Found", "text/plain; charset=utf-8");
                return;
            }
            if (!IsLoopbackRequest(call.Http))
            {
                await HttpUtil.SendAsync(call.Http, 403, """{"ok":false,"reason":"旧版初始化迁移仅限本机回环"}""", "application/json; charset=utf-8");
                return;
            }
            if (!api.Auth.NeedSetup)
            {
                await HttpUtil.SendAsync(call.Http, 409, """{"ok":false,"reason":"已经有主人了"}""", "application/json; charset=utf-8");
                return;
            }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason) = api.Auth.SetupOwner(b["username"]?.ToString(), b["password"]?.ToString());
            if (!ok)
            {
                // 一次性冲突（含并发败者）→ 409；账本故障 → 503 显式暴露；其余参数问题 → 400
                var status = reason == "已经有主人了" ? 409
                    : reason != null && reason == api.Auth.AccountFileIssue ? 503
                    : 400;
                await HttpUtil.SendAsync(call.Http, status, new JsonObject { ["ok"] = false, ["reason"] = reason }.ToJsonString(), "application/json; charset=utf-8");
                return;
            }
            var extra = new JsonObject();
            if (b["botName"] is JsonNode bn && !string.IsNullOrEmpty(bn.ToString())) extra["botName"] = Cut(bn.ToString(), 40);
            if (b["botTag"] is JsonNode bt && !string.IsNullOrEmpty(bt.ToString())) extra["botTag"] = Cut(bt.ToString(), 60);
            if (b["ownerName"] is JsonNode on && !string.IsNullOrEmpty(on.ToString())) extra["ownerName"] = Cut(on.ToString(), 40);
            if (b["themeMode"] is JsonNode tm && !string.IsNullOrEmpty(tm.ToString()))
                extra["theme"] = new JsonObject { ["mode"] = tm.ToString(), ["background"] = "" };
            if (b["persona"] is JsonNode pe && !string.IsNullOrEmpty(pe.ToString()))
                extra["persona"] = new JsonObject { ["current"] = Cut(pe.ToString(), 60) };

            if (extra["botName"] is null)
            {
                var personaId = b["persona"]?.ToString() ?? "dafeiyu";
                var hit = personas.List().FirstOrDefault(p => p.Id == personaId);
                if (hit is not null) extra["botName"] = hit.Name;
            }
            if (b["model"] is JsonObject model && (!string.IsNullOrEmpty(model["provider"]?.ToString()) || !string.IsNullOrEmpty(model["model"]?.ToString())))
            {
                var effort = model["reasoningEffort"]?.ToString() ?? "low";
                if (effort is not ("off" or "low" or "medium" or "high")) effort = "low";
                extra["model"] = new JsonObject
                {
                    ["provider"] = model["provider"]?.ToString() ?? "",
                    ["model"] = model["model"]?.ToString() ?? "",
                    ["reasoningEffort"] = effort,
                };
            }

            var merged = (JsonObject)api.Cfg.DeepClone();
            foreach (var (k, v) in extra) merged[k] = v?.DeepClone();
            merged["setupDone"] = true;
            api.Config.Save(merged);

            var avatarNode = b["avatar"];
            var rawAvatar = HttpUtil.StripDataUrl(avatarNode is JsonObject av ? av["data"]?.ToString() : avatarNode?.ToString());
            if (!string.IsNullOrEmpty(rawAvatar))
            {
                try
                {
                    File.WriteAllBytes(api.Store.PathIn("avatar"), Convert.FromBase64String(rawAvatar));
                    var mime = (avatarNode as JsonObject)?["mime"]?.ToString() ?? "image/png";
                    File.WriteAllText(api.Store.PathIn("avatar.mime"), mime);
                }
                catch (Exception ex) { api.Log.Child("setup").Warn("向导存头像失败：" + ex.Message); }
            }

            // 迁移完成：进程内自动清除开关——此后新请求回到 404；想重开也会被「已有主人」409 顶住（一次性）
            Environment.SetEnvironmentVariable(LegacySetupSwitch, null);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        // ---------- 模型列表（独立版：来自本地配置的 provider） ----------
        // t18 MED-004 + t28 收紧：目录与 detail 一律要求 Bearer，NeedSetup=true（向导态）不再豁免
        r.Add("GET", "api/models", async call =>
        {
            if (await api.RequireUserAsync(call) is null) return;
            var catalog = api.Get<IModelCatalog>();
            var models = catalog is null ? [] : await catalog.ListModelsAsync();
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["models"] = models,
                ["default"] = catalog?.DefaultModel(),
            });
        });

        // ---------- 人设卡 ----------
        // t18 MED-004 + t28 收紧：目录与 detail 一律要求 Bearer，NeedSetup=true（向导态）不再豁免
        r.Add("GET", "api/personas", async call =>
        {
            if (await api.RequireUserAsync(call) is null) return;
            var arr = new JsonArray();
            foreach (var p in personas.List())
            {
                arr.Add(new JsonObject
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                });
            }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["personas"] = arr });
        });

        r.Add("GET", "api/personas/detail", async call =>
        {
            if (await api.RequireUserAsync(call) is null) return;
            var id = HttpUtil.Query(call.Http, "id") ?? "";
            var one = id.Length > 0 ? personas.Read(id) : null;
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["persona"] = one is null ? null : new JsonObject
                {
                    ["id"] = one.Id,
                    ["name"] = one.Name,
                    ["tags"] = new JsonArray(one.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                    ["body"] = one.Body,
                },
            });
        });

        r.Add("POST", "api/personas/save", async call =>
        {
            if (!api.Auth.NeedSetup && await api.RequireOwnerAsync(call) is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            try
            {
                var tags = new List<string>();
                if (b["tags"] is JsonArray arr)
                    tags.AddRange(arr.Select(t => t?.ToString()?.Trim() ?? "").Where(t => t.Length > 0));
                else
                    tags.AddRange((b["tags"]?.ToString() ?? "").Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries));

                var saved = personas.Save(
                    b["id"]?.ToString()?.Trim() is { Length: > 0 } rawId ? rawId : b["name"]?.ToString()?.Trim(),
                    b["name"]?.ToString()?.Trim(),
                    tags,
                    b["body"]?.ToString());

                await HttpUtil.JsonAsync(call.Http, new JsonObject
                {
                    ["ok"] = true,
                    ["persona"] = new JsonObject
                    {
                        ["id"] = saved.Id,
                        ["name"] = saved.Name,
                        ["tags"] = new JsonArray(saved.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                    },
                });
            }
            catch (Exception ex)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = ex.Message });
            }
        });

        r.Add("POST", "api/personas/delete", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var id = b["id"]?.ToString() ?? "";
            if (id.Length == 0) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "没说要删哪个" }); return; }
            if (id == api.Str("persona", "current"))
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "这个是当前正在用的人设，先换一个再删" });
                return;
            }
            personas.Delete(id);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        // ---------- 登录 / 登出 ----------
        r.Add("POST", "api/login", async call =>
        {
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason, acc) = api.Auth.Verify(b["username"]?.ToString(), b["password"]?.ToString());
            if (!ok || acc is null)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = reason ?? "用户名或密码不正确" });
                return;
            }
            var token = api.Auth.IssueSession(acc);
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["token"] = token,
                ["subject"] = acc.Subject,
                ["role"] = acc.Role,
                ["name"] = api.Auth.DisplayNameOf(acc.Subject),
            });
        });

        r.Add("POST", "api/logout", async call =>
        {
            api.Auth.DropSession(HttpUtil.TokenOf(call.Http));
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        // ---------- 浏览器桥接票（openInBrowser 的无凭证深链）----------
        // 会话 token 永不进 URL：先拿一张 60 秒一次性票，新窗口用它换回会话，之后地址栏里什么凭证都没有
        r.Add("POST", "api/bridge", async call =>
        {
            var me = api.UserOf(call.Http);
            if (me is null) { await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"没登录"}""", "application/json; charset=utf-8"); return; }
            var ticket = api.Auth.IssueBridgeTicket(HttpUtil.TokenOf(call.Http)!);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["ticket"] = ticket, ["ttl"] = AuraChat.Core.Auth.AuthService.BridgeTicketTtlSeconds });
        });

        r.Add("POST", "api/bridge/use", async call =>
        {
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var token = api.Auth.ConsumeBridgeTicket(b["ticket"]?.ToString());
            if (string.IsNullOrEmpty(token))
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "桥接票无效或已过期，请重新在原窗口打开" });
                return;
            }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["token"] = token });
        });

        r.Add("POST", "api/account/password", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason) = api.Auth.ChangePassword(me.Subject, b["oldPassword"]?.ToString(), b["newPassword"]?.ToString());
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = ok, ["reason"] = reason });
        });

        // 访客路由已退役（新版无访客机制，/api/guests/* 自动返回 404）
    }

    /// <summary>t31/t32：旧主人初始化迁移开关（进程环境变量，仅显式 =1 打开；迁移成功后自动清除）。</summary>
    internal const string LegacySetupSwitch = "AURACHAT_ALLOW_LEGACY_SETUP";

    /// <summary>请求是否来自本机回环（127.0.0.1/::1）。只看 TCP 对端地址，不读任何转发头。</summary>
    internal static bool IsLoopbackRequest(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    /// <summary>
    /// /api/state 是否收到了「显式但不是 Bearer token」的凭证（t32）：非空 Bearer/cookie 由 presented 判定，
    /// 这里兜住 Basic 等外来方案——带了凭证却不被认可就该 401，不能装作没看见；
    /// 仅 scheme 名或空 token（"Bearer "）与没带等价，仍走匿名壳。
    /// </summary>
    private static bool HasExplicitCredential(string authz, string? presented)
        => !string.IsNullOrWhiteSpace(presented)
           || (!string.IsNullOrWhiteSpace(authz) && !authz.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase));

    private static string NonEmpty(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
