namespace AuraChat.Features.Life;

/// <summary>
/// 生命状态：state.json 的数据模型（唯一事实源）。
/// 设计原则：生理层（激素、能量、饥饿等）→ 情绪层 → 表达层。
/// 基于"凌曦沫的养鱼小课堂 - 生命状态完整实施指南v2"。
/// </summary>
public sealed class LifeState
{
    /// <summary>多巴胺（兴致）：0-100，基线 50。</summary>
    public double Dopamine { get; set; }

    /// <summary>皮质醇（张力/压力）：0-100，基线 20。</summary>
    public double Cortisol { get; set; }

    /// <summary>血清素（稳定度）：0-100，基线 50。</summary>
    public double Serotonin { get; set; }

    /// <summary>能量：0-100，每小时 -3。</summary>
    public double Energy { get; set; }

    /// <summary>饥饿：0-100，每小时 +2。</summary>
    public double Hunger { get; set; }

    /// <summary>口渴：0-100，每小时 +5。</summary>
    public double Thirst { get; set; }

    /// <summary>困倦：0-100，每小时 +1.5。</summary>
    public double Sleepiness { get; set; }

    /// <summary>状态时间戳（毫秒）：上次更新的时间。</summary>
    public long At { get; set; }

    /// <summary>待处理事件队列：每个事件只应用一次（幂等性保证）。</summary>
    public List<LifeEvent> PendingEvents { get; set; } = new();

    /// <summary>事件冷却记录：key=事件类型，value=下次可触发时间戳。</summary>
    public Dictionary<string, long> Cooldowns { get; set; } = new();

    /// <summary>创建默认基线状态。</summary>
    public static LifeState CreateBaseline(long nowMs)
    {
        return new LifeState
        {
            Dopamine = HormoneMath.DopamineBase,
            Cortisol = HormoneMath.CortisolBase,
            Serotonin = HormoneMath.SerotoninBase,
            Energy = 80,
            Hunger = 20,
            Thirst = 15,
            Sleepiness = 10,
            At = nowMs,
            PendingEvents = new List<LifeEvent>(),
            Cooldowns = new Dictionary<string, long>()
        };
    }
}

/// <summary>
/// 生命事件：用户触发的一次性动作（喝水、吃饭、安慰、微调等）。
/// 每个事件有唯一 id，只被应用一次，保证幂等性。
/// </summary>
public sealed class LifeEvent
{
    /// <summary>事件唯一 ID（GUID）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>事件类型：drink/eat/comfort/nudge。</summary>
    public string Type { get; set; } = "";

    /// <summary>事件触发时间戳（毫秒）。</summary>
    public long TriggeredAt { get; set; }

    /// <summary>事件参数（可选，如 nudge 的 delta 值）。</summary>
    public Dictionary<string, double> Args { get; set; } = new();

    /// <summary>事件原因/描述（用于日志和历史记录）。</summary>
    public string Reason { get; set; } = "";
}

/// <summary>
/// 事件冷却配置：定义每种事件的冷却时间。
/// </summary>
public static class EventCooldowns
{
    /// <summary>喝水冷却：3 分钟（180 秒）。</summary>
    public const long DrinkCooldownMs = 180_000;

    /// <summary>吃饭冷却：1 小时（3600 秒）。</summary>
    public const long EatCooldownMs = 3_600_000;

    /// <summary>安慰冷却：5 分钟（300 秒）。</summary>
    public const long ComfortCooldownMs = 300_000;

    /// <summary>微调冷却：1 分钟（60 秒）。</summary>
    public const long NudgeCooldownMs = 60_000;

    /// <summary>获取事件类型的冷却时间。</summary>
    public static long GetCooldown(string eventType) => eventType switch
    {
        "drink" => DrinkCooldownMs,
        "eat" => EatCooldownMs,
        "comfort" => ComfortCooldownMs,
        "nudge" => NudgeCooldownMs,
        _ => 0
    };
}
