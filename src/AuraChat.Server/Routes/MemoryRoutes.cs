using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using AuraChat.Core.Features;

namespace AuraChat.Server.Routes;

/// <summary>记忆面板 + 心情（对齐原 web/routes/memory.js 的 12 条接口）。</summary>
[RouteModule(60)]
public static class MemoryRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        // ---------- 记忆 ----------
        r.Add("GET", "api/memory", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }

            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["stats"] = memory.Stats(),
                ["items"] = memory.List(),
                ["insights"] = memory.Insights(),
                ["styles"] = memory.Styles(),
                ["profiles"] = memory.Profiles(),
                ["recalls"] = memory.Recalls(),
                ["lastConsolidate"] = memory.LastConsolidate(),
            });
        });

        r.Add("POST", "api/memory", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var tags = b["tags"] is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0) : Array.Empty<string>();
            await HttpUtil.JsonAsync(call.Http, memory.Add(
                b["text"]?.ToString()?.Trim() ?? "",
                AuraChat.Core.JsonUtil.Num(b["importance"], 0.6),
                tags,
                b["source"]?.ToString() ?? "manual"));
        });

        r.Add("POST", "api/memory/delete", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            await HttpUtil.JsonAsync(call.Http, memory.Remove(b["id"]?.ToString() ?? ""));
        });

        r.Add("POST", "api/memory/profile", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var about = b["about"]?.ToString()?.Trim() ?? "";
            if (about.Length > 0 && about != "owner")
            {
                // 账号用户名 → accountId（按账号账本查找，访客机制已退役）
                var acc = api.Get<AccountService>()?.FindByUsername(about);
                if (acc is not null) about = acc.AccountId;
                // 注意：传入 "guest-*" 字符串会直接作为 about 键使用（只读兼容，不做映射）
            }
            await HttpUtil.JsonAsync(call.Http, memory.SetProfile(about, b["text"]?.ToString() ?? "", b["name"]?.ToString() ?? ""));
        });

        r.Add("POST", "api/memory/style", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            await HttpUtil.JsonAsync(call.Http, memory.SetStyle(b["who"]?.ToString() ?? "self", b["lines"] as JsonArray));
        });

        r.Add("POST", "api/memory/insight", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var key = (b["key"]?.ToString() ?? b["who"]?.ToString() ?? "").Trim();
            if (key.Length == 0) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["message"] = "没说是谁" }); return; }
            await HttpUtil.JsonAsync(call.Http, memory.SetInsights(key, b["lines"] as JsonArray, b["name"]?.ToString() ?? ""));
        });

        r.Add("POST", "api/memory/update", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var tags = b["tags"] is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0) : null;
            await HttpUtil.JsonAsync(call.Http, memory.Update(
                b["id"]?.ToString() ?? "",
                b["text"]?.ToString(),
                b["importance"] is null ? null : AuraChat.Core.JsonUtil.Num(b["importance"], 0.6),
                tags));
        });

        r.Add("POST", "api/memory/consolidate", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var memory = api.Get<IMemoryService>();
            if (memory is null) { await HttpUtil.Fail(call.Http, "记忆模块没装上"); return; }
            try
            {
                var r2 = await memory.ConsolidateAsync(true, true, call.Http.RequestAborted);
                r2["ok"] = true;
                await HttpUtil.JsonAsync(call.Http, r2);
            }
            catch (Exception ex)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = ex.Message });
            }
        });

        // ---------- 心情 ----------
        r.Add("GET", "api/mood", async call =>
        {
            if (await api.RequireUserAsync(call) is null) return;
            var mood = api.Get<IMoodService>();
            if (mood is null) { await HttpUtil.Fail(call.Http, "心情模块没装上"); return; }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["state"] = mood.State(), ["history"] = mood.History() });
        });

        r.Add("POST", "api/mood", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var mood = api.Get<IMoodService>();
            if (mood is null) { await HttpUtil.Fail(call.Http, "心情模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            if (AuraChat.Core.JsonUtil.Bool(b["reset"], false)) mood.Reset();
            else
            {
                // 手动调整只动「兴致」，张力和踏实度由聊天自己长（原版注释）。
                var d = Math.Clamp(AuraChat.Core.JsonUtil.Num(b["delta"], 0), -50, 50);
                if (d != 0)
                {
                    var reason = (b["reason"]?.ToString() ?? "手动调整");
                    if (reason.Length > 40) reason = reason[..40];
                    mood.Nudge(d, reason);
                }
            }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["state"] = mood.State() });
        });
    }
}
