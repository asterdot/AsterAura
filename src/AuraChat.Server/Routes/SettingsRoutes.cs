using System.Text.Json.Nodes;
using AuraChat.Core.Ai;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;

namespace AuraChat.Server.Routes;

/// <summary>设置面板：配置读写、日志、状态、手动测试、自驱备忘、冲浪兴趣（对齐原 web/routes/settings.js）。</summary>
[RouteModule(20)]
public static class SettingsRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        var personas = api.Get<PersonaService>() ?? new PersonaService(api.Store, api.PersonasRoot);

        r.Add("GET", "api/config", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["config"] = api.Config.PublicView(),
                ["owner"] = api.Auth.OwnerInfo(),
            });
        });

        r.Add("PATCH", "api/config", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);

            var personaId = (b["persona"] as JsonObject)?["current"]?.ToString();
            if (!string.IsNullOrEmpty(personaId) && b["botName"] is null)
            {
                var hit = personas.List().FirstOrDefault(p => p.Id == personaId);
                if (hit is not null) b["botName"] = hit.Name;
            }

            api.Config.MergeFromPanel(b);
            var host = api.Str("web", "host");
            var port = (int)api.Num(2332, "web", "port");
            if (api.Host is { BoundPort: var boundPort, BoundHost: var boundHost }
                && (boundPort != port || boundHost != host))
                _ = api.Host.RebindAsync();

            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["config"] = api.Config.PublicView() });
        });

        r.Add("GET", "api/logs", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var entries = new JsonArray();
            foreach (var e in api.Log.Recent(300))
            {
                entries.Add(new JsonObject
                {
                    ["t"] = e.T,
                    ["time"] = e.Time,
                    ["level"] = e.Level,
                    ["area"] = e.Area,
                    ["text"] = e.Text,
                });
            }
            var (count, bytes, dropped) = api.Log.Stats();
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["version"] = api.Version,
                ["logs"] = entries,
                ["stats"] = new JsonObject { ["count"] = count, ["bytes"] = bytes, ["dropped"] = dropped },
                ["maxMB"] = api.Num(2, "logs", "maxMB"),
            });
        });

        r.Add("POST", "api/logs/clear", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var n = api.Log.Clear();
            api.Log.Child("web").Info($"日志已清空（{n} 条）");
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["cleared"] = n });
        });

        r.Add("GET", "api/status", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;

            var result = new JsonObject { ["ok"] = true };
            foreach (var provider in api.StatusProviders)
            {
                try { result[provider.Key] = provider.Snapshot(); }
                catch (Exception ex) { result[provider.Key] = new JsonObject { ["ok"] = false, ["reason"] = ex.Message }; }
            }

            var models = api.Get<IModelCatalog>();
            result["models"] = models is null ? null : await models.ListModelsAsync();

            await HttpUtil.JsonAsync(call.Http, result);
        });

        r.Add("POST", "api/test", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var what = b["what"]?.ToString() ?? "";
            try
            {
                if (what == "speakup")
                {
                    var action = api.TestActions.FirstOrDefault(a => a.Name == "speakup");
                    if (action is null) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "主动搭话模块没装上" }); return; }
                    await action.RunAsync();
                    await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["message"] = "叫她了" });
                    return;
                }

                var hit = api.TestActions.FirstOrDefault(a => a.Name == what);
                if (hit is null)
                {
                    await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "不认识：" + what });
                    return;
                }
                var payload = await hit.RunAsync();
                payload["ok"] = true;
                await HttpUtil.JsonAsync(call.Http, payload);
            }
            catch (Exception ex)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = ex.Message });
            }
        });

        r.Add("GET", "api/selfdrive", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var sd = api.Get<ISelfDriveMemo>();
            if (sd is null) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "自驱模块没装上" }); return; }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["text"] = sd.ReadMemo(), ["state"] = sd.State() });
        });

        r.Add("POST", "api/selfdrive", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var sd = api.Get<ISelfDriveMemo>();
            if (sd is null) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "自驱模块没装上" }); return; }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            await HttpUtil.JsonAsync(call.Http, sd.WriteMemo(b["text"]?.ToString()));
        });

        r.Add("GET", "api/surf", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var surf = api.Get<ISurfModule>();
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["interests"] = surf?.ListInterests() ?? [] });
        });

        r.Add("POST", "api/surf", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var surf = api.Get<ISurfModule>();
            if (surf is null) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "冲浪模块没装上" }); return; }
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            switch (b["action"]?.ToString())
            {
                case "add": await HttpUtil.JsonAsync(call.Http, surf.AddInterest(b["topic"]?.ToString(), b["why"]?.ToString())); break;
                case "note": await HttpUtil.JsonAsync(call.Http, surf.NoteInterest(b["topic"]?.ToString(), b["note"]?.ToString())); break;
                default: await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "不认识：" + (b["action"]?.ToString() ?? "") }); break;
            }
        });
    }
}
