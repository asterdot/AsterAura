using System.Text.Json;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Life;

/// <summary>
/// 生命状态服务：state.json 作为唯一事实源，负责时间驱动的心跳和事件响应。
/// 设计原则：drift(时间漂移) + react(事件反应) → system prompt 注入 + 面板展示。
/// 基于"凌曦沫的养鱼小课堂 - 生命状态完整实施指南v2"。
/// </summary>
public sealed class LifeStateService : IDisposable
{
    private readonly FeatureHost _host;
    private readonly string _file;
    private readonly Lock _gate = new();
    private LifeState _state;
    private Timer? _heartbeat;
    private bool _disposed;

    public LifeStateService(FeatureHost host)
    {
        _host = host;
        _file = host.Store.PathIn("state.json");
        _state = Load();
        StartHeartbeat();
    }

    private bool Enabled => _host.Flag(true, "life", "enabled");
    private bool HeartbeatEnabled => _host.Flag(true, "life", "heartbeat");
    private AreaLog Log => _host.Log.Child("life");

    /// <summary>当前毫秒时间戳。</summary>
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    #region 状态文件读写（原子操作）

    /// <summary>
    /// 加载状态文件：不存在时创建基线，存在时读取并验证。
    /// </summary>
    private LifeState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_file))
            {
                var baseline = LifeState.CreateBaseline(NowMs());
                SaveInternal(baseline, "初始化");
                return baseline;
            }

            try
            {
                var json = File.ReadAllText(_file);
                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj == null)
                {
                    Log.Warn("state.json 解析失败，使用基线");
                    return LifeState.CreateBaseline(NowMs());
                }

                return DeserializeState(obj);
            }
            catch (Exception ex)
            {
                Log.Warn($"加载 state.json 失败：{ex.Message}，使用基线");
                return LifeState.CreateBaseline(NowMs());
            }
        }
    }

    /// <summary>
    /// 保存状态文件：原子写入（.tmp + rename），失败不抛异常。
    /// </summary>
    private void SaveInternal(LifeState state, string reason)
    {
        try
        {
            var obj = SerializeState(state);
            obj["reason"] = reason;
            obj["savedAt"] = NowMs();

            var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var tmpFile = _file + ".tmp";

            // 写入临时文件
            File.WriteAllText(tmpFile, json);

            // 原子替换
            File.Move(tmpFile, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写入失败不能导致聊天失败，只记录日志
            Log.Warn($"保存 state.json 失败：{ex.Message}");
        }
    }

    /// <summary>序列化状态为 JSON 对象。</summary>
    private static JsonObject SerializeState(LifeState state)
    {
        var obj = new JsonObject
        {
            ["dopamine"] = state.Dopamine,
            ["cortisol"] = state.Cortisol,
            ["serotonin"] = state.Serotonin,
            ["energy"] = state.Energy,
            ["hunger"] = state.Hunger,
            ["thirst"] = state.Thirst,
            ["sleepiness"] = state.Sleepiness,
            ["at"] = state.At,
            ["cooldowns"] = new JsonObject()
        };

        // 序列化冷却记录
        var cooldowns = obj["cooldowns"]!.AsObject();
        foreach (var (key, value) in state.Cooldowns)
        {
            cooldowns[key] = value;
        }

        // 序列化待处理事件
        var events = new JsonArray();
        foreach (var evt in state.PendingEvents)
        {
            var evtObj = new JsonObject
            {
                ["id"] = evt.Id,
                ["type"] = evt.Type,
                ["triggeredAt"] = evt.TriggeredAt,
                ["reason"] = evt.Reason
            };

            if (evt.Args.Count > 0)
            {
                var args = new JsonObject();
                foreach (var (k, v) in evt.Args)
                {
                    args[k] = v;
                }
                evtObj["args"] = args;
            }

            events.Add(evtObj);
        }
        obj["pendingEvents"] = events;

        return obj;
    }

    /// <summary>反序列化 JSON 对象为状态。</summary>
    private static LifeState DeserializeState(JsonObject obj)
    {
        var state = new LifeState
        {
            Dopamine = JsonUtil.Num(obj["dopamine"], HormoneMath.DopamineBase),
            Cortisol = JsonUtil.Num(obj["cortisol"], HormoneMath.CortisolBase),
            Serotonin = JsonUtil.Num(obj["serotonin"], HormoneMath.SerotoninBase),
            Energy = JsonUtil.Num(obj["energy"], 80),
            Hunger = JsonUtil.Num(obj["hunger"], 20),
            Thirst = JsonUtil.Num(obj["thirst"], 15),
            Sleepiness = JsonUtil.Num(obj["sleepiness"], 10),
            At = (long)JsonUtil.Num(obj["at"], NowMs())
        };

        // 反序列化冷却记录
        if (obj["cooldowns"] is JsonObject cooldowns)
        {
            foreach (var (key, value) in cooldowns)
            {
                if (value != null)
                {
                    state.Cooldowns[key] = (long)JsonUtil.Num(value, 0);
                }
            }
        }

        // 反序列化待处理事件
        if (obj["pendingEvents"] is JsonArray events)
        {
            foreach (var item in events)
            {
                if (item is not JsonObject evtObj) continue;

                var evt = new LifeEvent
                {
                    Id = JsonUtil.Str(evtObj["id"], Guid.NewGuid().ToString()),
                    Type = JsonUtil.Str(evtObj["type"], ""),
                    TriggeredAt = (long)JsonUtil.Num(evtObj["triggeredAt"], NowMs()),
                    Reason = JsonUtil.Str(evtObj["reason"], "")
                };

                if (evtObj["args"] is JsonObject args)
                {
                    foreach (var (k, v) in args)
                    {
                        if (v != null)
                        {
                            evt.Args[k] = JsonUtil.Num(v, 0);
                        }
                    }
                }

                state.PendingEvents.Add(evt);
            }
        }

        return state;
    }

    #endregion

    #region 心跳机制（60 秒）

    /// <summary>启动心跳定时器：60 秒触发一次 drift + react。</summary>
    private void StartHeartbeat()
    {
        if (!HeartbeatEnabled) return;

        _heartbeat = new Timer(_ =>
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Warn($"心跳失败：{ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        Log.Info("生命状态心跳已启动（60 秒间隔）");
    }

    /// <summary>
    /// 心跳核心：drift（时间漂移）+ react（事件响应）。
    /// </summary>
    private void Tick()
    {
        if (!Enabled) return;

        lock (_gate)
        {
            var nowMs = NowMs();
            var next = _state;

            // 1. drift：时间漂移（激素衰减 + 生理状态累积）
            next = Drift(next, nowMs);

            // 2. react：处理待处理事件
            next = React(next, nowMs);

            // 3. 保存状态
            if (next != _state)
            {
                _state = next;
                SaveInternal(_state, "心跳更新");
            }
        }
    }

    #endregion

    #region drift: 时间漂移

    /// <summary>
    /// drift：时间漂移逻辑。
    /// - 激素向基线衰减（半衰期）
    /// - 能量每小时 -3
    /// - 饥饿每小时 +2
    /// - 口渴每小时 +5
    /// - 困倦每小时 +1.5
    /// </summary>
    private static LifeState Drift(LifeState state, long nowMs)
    {
        var minutes = Math.Max(0, (nowMs - state.At) / 60_000.0);
        if (minutes < 0.1) return state; // 时间漂移小于 6 秒，跳过

        var hours = minutes / 60.0;

        // 激素衰减
        var hormones = HormoneMath.Decayed(
            new Hormones(state.Dopamine, state.Cortisol, state.Serotonin),
            state.At,
            nowMs);

        // 生理状态漂移
        var energy = Math.Clamp(state.Energy - hours * 3, 0, 100);
        var hunger = Math.Clamp(state.Hunger + hours * 2, 0, 100);
        var thirst = Math.Clamp(state.Thirst + hours * 5, 0, 100);
        var sleepiness = Math.Clamp(state.Sleepiness + hours * 1.5, 0, 100);

        return new LifeState
        {
            Dopamine = hormones.Dopamine,
            Cortisol = hormones.Cortisol,
            Serotonin = hormones.Serotonin,
            Energy = energy,
            Hunger = hunger,
            Thirst = thirst,
            Sleepiness = sleepiness,
            At = nowMs,
            PendingEvents = state.PendingEvents,
            Cooldowns = state.Cooldowns
        };
    }

    #endregion

    #region react: 事件响应

    /// <summary>
    /// react：处理待处理事件。
    /// - 每个事件只应用一次（幂等性保证）
    /// - 应用后从队列移除
    /// - 记录冷却时间
    /// </summary>
    private static LifeState React(LifeState state, long nowMs)
    {
        if (state.PendingEvents.Count == 0) return state;

        var next = state;
        var processedIds = new HashSet<string>();

        foreach (var evt in state.PendingEvents)
        {
            if (processedIds.Contains(evt.Id)) continue; // 防止重复处理

            next = ApplyEvent(next, evt, nowMs);
            processedIds.Add(evt.Id);
        }

        // 移除已处理的事件
        next.PendingEvents = next.PendingEvents.Where(e => !processedIds.Contains(e.Id)).ToList();

        return next;
    }

    /// <summary>应用单个事件的效果。</summary>
    private static LifeState ApplyEvent(LifeState state, LifeEvent evt, long nowMs)
    {
        // 创建新状态副本（深拷贝关键集合）
        var next = new LifeState
        {
            Dopamine = state.Dopamine,
            Cortisol = state.Cortisol,
            Serotonin = state.Serotonin,
            Energy = state.Energy,
            Hunger = state.Hunger,
            Thirst = state.Thirst,
            Sleepiness = state.Sleepiness,
            At = state.At,
            PendingEvents = new List<LifeEvent>(state.PendingEvents),
            Cooldowns = new Dictionary<string, long>(state.Cooldowns)
        };

        switch (evt.Type)
        {
            case "drink":
                next.Thirst = Math.Clamp(next.Thirst - 30, 0, 100);
                next.Cooldowns["drink"] = nowMs + EventCooldowns.DrinkCooldownMs;
                break;

            case "eat":
                next.Hunger = Math.Clamp(next.Hunger - 40, 0, 100);
                next.Energy = Math.Clamp(next.Energy + 20, 0, 100);
                next.Cooldowns["eat"] = nowMs + EventCooldowns.EatCooldownMs;
                break;

            case "comfort":
                var comfortDelta = new Hormones(8, -12, 10);
                var comfortResult = HormoneMath.ApplyDeltas(
                    new Hormones(next.Dopamine, next.Cortisol, next.Serotonin),
                    comfortDelta);
                next.Dopamine = comfortResult.Dopamine;
                next.Cortisol = comfortResult.Cortisol;
                next.Serotonin = comfortResult.Serotonin;
                next.Cooldowns["comfort"] = nowMs + EventCooldowns.ComfortCooldownMs;
                break;

            case "nudge":
                var delta = evt.Args.GetValueOrDefault("delta", 0);
                delta = Math.Clamp(delta, -50, 50);
                var nudgeDelta = new Hormones(delta, 0, 0);
                var nudgeResult = HormoneMath.ApplyDeltas(
                    new Hormones(next.Dopamine, next.Cortisol, next.Serotonin),
                    nudgeDelta);
                next.Dopamine = nudgeResult.Dopamine;
                next.Cooldowns["nudge"] = nowMs + EventCooldowns.NudgeCooldownMs;
                break;
        }

        return next;
    }

    #endregion

    #region 公共接口

    /// <summary>获取当前状态快照（带 drift 计算）。</summary>
    public LifeState GetCurrentState()
    {
        lock (_gate)
        {
            var nowMs = NowMs();
            return Drift(_state, nowMs);
        }
    }

    /// <summary>获取当前激素状态（用于 MoodService 集成）。</summary>
    public Hormones GetCurrentHormones()
    {
        var state = GetCurrentState();
        return new Hormones(state.Dopamine, state.Cortisol, state.Serotonin);
    }

    /// <summary>触发事件：添加到待处理队列，检查冷却。</summary>
    public bool TriggerEvent(string eventType, Dictionary<string, double>? args = null, string reason = "")
    {
        if (!Enabled) return false;

        lock (_gate)
        {
            var nowMs = NowMs();

            // 检查冷却
            if (_state.Cooldowns.TryGetValue(eventType, out var cooldownUntil))
            {
                if (nowMs < cooldownUntil)
                {
                    Log.Info($"事件 {eventType} 冷却中，剩余 {(cooldownUntil - nowMs) / 1000} 秒");
                    return false;
                }
            }

            // 添加事件到队列
            var evt = new LifeEvent
            {
                Id = Guid.NewGuid().ToString(),
                Type = eventType,
                TriggeredAt = nowMs,
                Args = args ?? new Dictionary<string, double>(),
                Reason = reason
            };

            _state.PendingEvents.Add(evt);
            
            // 立即处理事件（不等待心跳）
            _state = React(_state, nowMs);
            
            SaveInternal(_state, $"触发事件: {eventType}");

            Log.Info($"事件已触发并立即处理: {eventType} - {reason}");
            return true;
        }
    }

    /// <summary>获取状态快照（JSON 格式，用于 API）。</summary>
    public JsonObject StateSnapshot()
    {
        var state = GetCurrentState();
        var obj = SerializeState(state);
        obj["value"] = HormoneMath.SummaryValue(new Hormones(state.Dopamine, state.Cortisol, state.Serotonin));
        obj["enabled"] = Enabled;
        return obj;
    }

    /// <summary>重置为基线状态。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = LifeState.CreateBaseline(NowMs());
            SaveInternal(_state, "手动重置");
            Log.Info("状态已重置为基线");
        }
    }

    /// <summary>
    /// 由 MoodService.Save 回写激素：读优先 life，写也同步回 life，保证双向对称。
    /// 只更新三轴激素和时间戳，不碰 energy/hunger/thirst/sleepiness 等其他生命状态字段。
    /// </summary>
    public void UpdateHormones(Hormones h, string reason = "激素同步")
    {
        lock (_gate)
        {
            _state.Dopamine = h.Dopamine;
            _state.Cortisol = h.Cortisol;
            _state.Serotonin = h.Serotonin;
            _state.At = NowMs();
            SaveInternal(_state, reason);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeat?.Dispose();
        Log.Info("生命状态服务已停止");
    }
}
