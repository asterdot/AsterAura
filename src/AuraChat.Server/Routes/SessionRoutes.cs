using System.Text.Json.Nodes;
using AuraChat.Core.Ai;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;

namespace AuraChat.Server.Routes;

/// <summary>登录 / 初始化向导 / 人设 / 客人（对齐原 web/routes/session.js）。</summary>
[RouteModule(10)]
public static class SessionRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        var personas = api.Get<PersonaService>() ?? new PersonaService(api.Store, api.PersonasRoot);

        // ---------- 状态 ----------
        r.Add("GET", "api/state", async call =>
        {
            var me = api.UserOf(call.Http);
            var cfg = api.Cfg;
            var result = new JsonObject
            {
                ["ok"] = true,
                ["needSetup"] = api.Auth.NeedSetup,
                ["me"] = me is null ? null : new JsonObject
                {
                    ["subject"] = me.Subject,
                    ["role"] = me.Role,
                    ["name"] = api.Auth.DisplayNameOf(me.Subject),
                    ["level"] = string.IsNullOrEmpty(me.Level) ? (me.IsOwner ? "full" : "social") : me.Level,
                },
                ["botName"] = NonEmpty(api.Str("botName"), "AuraChat"),
                ["botTag"] = api.Str("botTag"),
                ["title"] = NonEmpty(api.Str("pageTitle"), "AuraChat"),
                ["siteName"] = NonEmpty(api.Str("siteName"), "AuraChat"),
                ["version"] = api.Version,
                ["ownerName"] = NonEmpty(api.Str("ownerName"), "主人"),
                ["persona"] = api.Str("persona", "current"),
                ["enabled"] = api.Flag(true, "enabled"),
                ["theme"] = cfg["theme"]?.DeepClone() ?? new JsonObject { ["mode"] = "follow", ["background"] = "" },
            };
            await HttpUtil.JsonAsync(call.Http, result);
        });

        // ---------- 初始化向导：建主人 ----------
        r.Add("POST", "api/setup", async call =>
        {
            if (!api.Auth.NeedSetup)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "已经有主人了" });
                return;
            }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason) = api.Auth.SetupOwner(b["username"]?.ToString(), b["password"]?.ToString());
            if (ok)
            {
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
            }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = ok, ["reason"] = reason });
        });

        // ---------- 模型列表（独立版：来自本地配置的 provider） ----------
        r.Add("GET", "api/models", async call =>
        {
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
        r.Add("GET", "api/personas", async call =>
        {
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
            if (!api.Auth.NeedSetup && await api.RequireUserAsync(call) is null) return;
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

        r.Add("POST", "api/account/password", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason) = api.Auth.ChangePassword(me.Subject, b["oldPassword"]?.ToString(), b["newPassword"]?.ToString());
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = ok, ["reason"] = reason });
        });

        // ---------- 客人 ----------
        r.Add("GET", "api/guests", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["guests"] = new JsonArray(api.Auth.ListGuests().Select(g => (JsonNode?)g).ToArray()),
            });
        });

        r.Add("POST", "api/guests", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason, subject, id, username, password) = api.Auth.CreateGuest(
                b["username"]?.ToString(), b["displayName"]?.ToString(), b["note"]?.ToString(), b["level"]?.ToString());
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = ok,
                ["reason"] = reason,
                ["subject"] = subject,
                ["id"] = id,
                ["username"] = username,
                ["password"] = password,
            });
        });

        r.Prefix("PATCH", "api/guests/", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var subject = Uri.UnescapeDataString(call.Path["api/guests/".Length..]);
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var (ok, reason) = api.Auth.UpdateGuest(subject, b);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = ok, ["reason"] = reason });
        });

        r.Prefix("DELETE", "api/guests/", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var subject = Uri.UnescapeDataString(call.Path["api/guests/".Length..]);
            var (ok, reason) = api.Auth.DeleteGuest(subject);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = ok, ["reason"] = reason });
        });
    }

    private static string NonEmpty(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
