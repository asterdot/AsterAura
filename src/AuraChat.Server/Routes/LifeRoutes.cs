using System.Text.Json.Nodes;
using AuraChat.Core.Features;
using AuraChat.Features.Life;

namespace AuraChat.Server.Routes;

/// <summary>生命状态接口：GET /api/life/state、POST /api/life/event。</summary>
[RouteModule(65)]
public static class LifeRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        // GET /api/life/state — 获取当前生命状态快照
        r.Add("GET", "api/life/state", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;

            var life = api.Get<LifeStateService>();
            if (life is null)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject
                {
                    ["ok"] = false,
                    ["reason"] = "生命状态模块未启动"
                });
                return;
            }

            var state = life.GetCurrentState();
            var hormones = life.GetCurrentHormones();

            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["state"] = new JsonObject
                {
                    ["energy"]     = state.Energy,
                    ["hunger"]     = state.Hunger,
                    ["thirst"]     = state.Thirst,
                    ["sleepiness"] = state.Sleepiness,
                },
                ["hormones"] = new JsonObject
                {
                    ["dopamine"]   = hormones.Dopamine,
                    ["serotonin"]  = hormones.Serotonin,
                    ["cortisol"]   = hormones.Cortisol,
                },
            });
        });

        // POST /api/life/event — 触发生命事件（drink/eat/comfort/nudge）
        r.Add("POST", "api/life/event", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;

            var life = api.Get<LifeStateService>();
            if (life is null)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject
                {
                    ["ok"] = false,
                    ["reason"] = "生命状态模块未启动"
                });
                return;
            }

            var body = await HttpUtil.ReadBodyAsync(call.Http);
            var type = body["type"]?.ToString() ?? "";

            var ok = life.TriggerEvent(type);
            var messages = new Dictionary<string, string>
            {
                ["drink"]   = "好，喝了水，感觉好多了～",
                ["eat"]     = "嗯，吃东西了，肚子不那么饿了",
                ["comfort"] = "谢谢你安慰我…",
                ["nudge"]   = "哎，被戳了一下！",
            };

            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"]      = ok,
                ["message"] = ok
                    ? (messages.TryGetValue(type, out var msg) ? msg : "好的")
                    : "事件不支持或在冷却中",
            });
        });
    }
}
