namespace AuraChat.Features.Life;

/// <summary>三激素：多巴胺（兴致）、皮质醇（张力）、血清素（稳定度）。</summary>
public readonly record struct Hormones(double Dopamine, double Cortisol, double Serotonin)
{
    public static Hormones Base => new(HormoneMath.DopamineBase, HormoneMath.CortisolBase, HormoneMath.SerotoninBase);
}

/// <summary>由激素派生出来的「怎么说话」的量化系数（原 hormones.js 的 deriveEffects）。</summary>
public readonly record struct HormoneEffects(double Sticker, double Voice, double Delay, double Chatty, double Verbosity);

/// <summary>
/// 三激素的纯计算：基线、半衰期衰减、事件抬升、行为式指令、温度劫持。
/// 设计原则只有一条：**状态永远不进入对话文本的名字里**，只被翻译成「怎么说话」。
/// 数值逐条照抄原 features/hormones.js。
/// </summary>
public static class HormoneMath
{
    // 基线：多巴胺 50 / 皮质醇 20 / 血清素 50
    public const double DopamineBase = 50;
    public const double CortisolBase = 20;
    public const double SerotoninBase = 50;

    // 半衰期（分钟）：皮质醇散得最快，血清素最慢。
    public const double DopamineHalfLifeMin = 45;
    public const double CortisolHalfLifeMin = 30;
    public const double SerotoninHalfLifeMin = 120;

    public static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    /// <summary>向基线回归：value 与 base 的差每过一个半衰期减半。</summary>
    public static double DecayToward(double value, double baseValue, double halfLifeMin, double minutes)
    {
        var m = Math.Max(0, minutes);
        var k = Math.Pow(0.5, m / Math.Max(1, halfLifeMin));
        return baseValue + (value - baseValue) * k;
    }

    /// <summary>按落盘时间把三个数衰减到「现在」。</summary>
    public static Hormones Decayed(Hormones state, long atMs, long nowMs)
    {
        var minutes = Math.Max(0, (nowMs - atMs) / 60_000.0);
        return new Hormones(
            Clamp(DecayToward(state.Dopamine, DopamineBase, DopamineHalfLifeMin, minutes), 0, 100),
            Clamp(DecayToward(state.Cortisol, CortisolBase, CortisolHalfLifeMin, minutes), 0, 100),
            Clamp(DecayToward(state.Serotonin, SerotoninBase, SerotoninHalfLifeMin, minutes), 0, 100));
    }

    /// <summary>在当前值上叠加一轮事件的增减；高张力会持续抽干稳定度。</summary>
    public static Hormones ApplyDeltas(Hormones state, Hormones deltas)
    {
        var h = new Hormones(
            Clamp(state.Dopamine + deltas.Dopamine, 0, 100),
            Clamp(state.Cortisol + deltas.Cortisol, 0, 100),
            Clamp(state.Serotonin + deltas.Serotonin, 0, 100));
        // 高张力会持续抽干稳定度：皮质醇越高，血清素掉得越快。
        if (h.Cortisol >= 70) h = h with { Serotonin = Clamp(h.Serotonin - 6, 0, 100) };
        return h;
    }

    /// <summary>把三个数翻译成「怎么说话」。只写动作，不写状态名。</summary>
    public static List<string> DeriveDirectives(Hormones h)
    {
        var output = new List<string>();
        var (d, c, s) = (h.Dopamine, h.Cortisol, h.Serotonin);

        if (c >= 70) output.Add("话很短，别开玩笑，别主动把话题往外扩；被追问的时候容易顶回去。");
        else if (c >= 55) output.Add("句子收紧，少用感叹号，别急着答应什么。");

        if (s <= 30) output.Add("没耐心重复解释，容易打断，回得生硬。");
        else if (s >= 70 && c < 55) output.Add("语气平，愿意耐心解释，对方说错了也不急着纠正。");

        if (d >= 70 && c < 70) output.Add("兴致高，联想跳得快，愿意多说两句、多举一个例子。");
        else if (d < 30) output.Add("提不起劲，语气淡，别假装热情，也别硬找话题。");

        return output;
    }

    /// <summary>句子长短 / 表情频率 / 回复延迟这些不用模型判断的东西，直接用数算。</summary>
    public static HormoneEffects DeriveEffects(Hormones h)
    {
        var (d, c, s) = (h.Dopamine, h.Cortisol, h.Serotonin);
        var energy = (d - 50) / 50;   // -1 .. 1
        var tension = (c - 20) / 80;  // 0 .. 1
        var calm = (s - 50) / 50;     // -1 .. 1

        var chatty = Clamp(1 + energy * 0.5 + calm * 0.2 - tension * 0.3, 0.35, 1.7);
        return new HormoneEffects(
            Sticker: Clamp(1 + energy * 0.5 - tension * 0.6, 0.15, 1.7),
            Voice: Clamp(1 + energy * 0.4 - tension * 0.5, 0.15, 1.6),
            Delay: Clamp(1 - energy * 0.15 + tension * 0.45, 0.7, 1.8),
            Chatty: chatty,
            Verbosity: Clamp(chatty, 0.35, 1.7));
    }

    /// <summary>
    /// 情绪劫持：张力拉满时压低温度（保守、防御），兴致拉满时抬高（发散）。
    /// 两者同时成立时张力优先——人在应激时不会变得天马行空。返回 null 表示不干预。
    /// </summary>
    public static double? HijackTemperature(Hormones h)
    {
        if (h.Cortisol >= 70) return 0.35;
        if (h.Dopamine >= 70 && h.Cortisol < 55) return 0.9;
        return null;
    }

    /// <summary>给界面看的一个 0-100 汇总值：兴致和稳定拉高，张力拉低。</summary>
    public static int SummaryValue(Hormones h)
        => (int)Math.Round(Clamp((h.Dopamine + h.Serotonin + (100 - h.Cortisol)) / 3, 0, 100));
}
