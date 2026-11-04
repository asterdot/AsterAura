using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Features;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Features.Chat;

namespace AuraChat.Server.Routes;

/// <summary>上下文调试窗口（C3）：owner 能看到"这一轮真的要发给模型的全部内容"。</summary>
[RouteModule(45)]
public static class DebugRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        var personas = api.Get<PersonaService>() ?? new PersonaService(api.Store, api.PersonasRoot);

        r.Add("GET", "api/debug/context", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;

            var env = api.Env;
            var cfg = api.Config.Current;

            var parts = new JsonObject
            {
                ["ok"] = true,
                ["at"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            };

            // 系统提示的每一段（人设/规则/心情/技能/记忆/压缩摘要）
            var systemParts = new JsonArray();
            try
            {
                var personaId = cfg["persona"]?["current"]?.ToString();
                systemParts.Add(new JsonObject
                {
                    ["name"] = "人设",
                    ["text"] = personas.CurrentText(personaId),
                });
                systemParts.Add(new JsonObject
                {
                    ["name"] = "规则",
                    ["text"] = PersonaService.RulesText("private", api.Config),
                });
                if (env.Get<IMoodService>() is { } mood)
                    systemParts.Add(new JsonObject { ["name"] = "心情行", ["text"] = mood.ContextLine() });
                foreach (var provider in ((FeatureHost)env).ContextProviders)
                {
                    foreach (var line in provider.ContextLines())
                        systemParts.Add(new JsonObject { ["name"] = provider.GetType().Name, ["text"] = line });
                }
                if (env.Get<IMemoryService>() is { } mem)
                    systemParts.Add(new JsonObject { ["name"] = "记忆召回（最近一次）", ["text"] = await mem.RecallAsync(AuraChatInfo.OwnerId, "", default) });
                if (env.Get<ContextCompressor>() is { } comp && comp.SummaryOf(AuraChatInfo.OwnerId) is { } s)
                    systemParts.Add(new JsonObject { ["name"] = "上下文压缩摘要", ["text"] = s["summary"]?.ToString() ?? "" });
            }
            catch (Exception ex)
            {
                systemParts.Add(new JsonObject { ["name"] = "（取段出错）", ["text"] = ex.Message });
            }
            parts["systemParts"] = systemParts;
            parts["systemChars"] = systemParts.Sum(p => (p as JsonObject)?["text"]?.ToString()?.Length ?? 0);

            // 最近原文窗口（上下文里真正带着的 N 条）
            try
            {
                var history = api.Get<IHistoryStore>();
                var recent = new JsonArray();
                if (history is not null)
                {
                    foreach (var m in history.Recent(AuraChatInfo.OwnerId, ChatHub.ContextMessageCount))
                    {
                        if (m is not JsonObject msg) continue;
                        recent.Add(new JsonObject
                        {
                            ["who"] = msg["who"]?.ToString() ?? "",
                            ["kind"] = msg["kind"]?.ToString() ?? "text",
                            ["t"] = msg["t"]?.DeepClone(),
                            ["text"] = msg["text"]?.ToString() ?? "",
                            ["hasMedia"] = msg["mediaId"] is not null,
                        });
                    }
                }
                parts["recentWindow"] = recent;
                parts["recentCount"] = recent.Count;
                parts["keepRecent"] = ChatHub.ContextMessageCount;
            }
            catch (Exception ex)
            {
                parts["recentError"] = ex.Message;
            }

            // 工具列表（她会拿到哪些工具）
            try
            {
                var tools = new JsonArray();
                if (env.Get<IToolRegistry>() is { } reg)
                {
                    foreach (var def in reg.All())
                        tools.Add(new JsonObject { ["name"] = def.Name });
                }
                parts["tools"] = tools;
                parts["toolCount"] = tools.Count;
            }
            catch (Exception ex)
            {
                parts["toolsError"] = ex.Message;
            }

            // 压缩状态
            if (env.Get<ContextCompressor>() is { } compressor)
            {
                parts["compress"] = compressor.SummaryOf(AuraChatInfo.OwnerId) ?? new JsonObject { ["none"] = true };
            }

            // 粗略 token 估算：中文 ≈ 1 字/token（保守），英文按 4 字符/token——只给个量级感
            long approxTokens = 0;
            foreach (var p in systemParts)
                approxTokens += (p as JsonObject)?["text"]?.ToString()?.Length ?? 0;
            foreach (var m in parts["recentWindow"] as JsonArray ?? [])
                approxTokens += ((m as JsonObject)?["text"]?.ToString() ?? "").Length;
            parts["approxTokens"] = approxTokens;

            await HttpUtil.JsonAsync(call.Http, parts);
        });

        r.Add("POST", "api/debug/compress", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var comp = api.Env.Get<ContextCompressor>();
            if (comp is null) { await HttpUtil.Fail(call.Http, "压缩模块没装上"); return; }
            var result = await comp.RunOnceAsync(default);
            await HttpUtil.JsonAsync(call.Http, result);
        });
    }
}
