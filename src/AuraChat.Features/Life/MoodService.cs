using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Media;

namespace AuraChat.Features.Life;

/// <summary>
/// 三激素情绪（原 features/companion.js 的 mood 半边）。
/// 落盘 mood.json：{dopamine,cortisol,serotonin,at,reason,history:[…]}；旧「value」字段自动迁移。
/// </summary>
public sealed partial class MoodService : IMoodService
{
    private static readonly string[] UpWords =
    [
        "喜欢你", "爱你", "想你", "可爱", "好看", "漂亮", "聪明", "真棒", "厉害", "牛", "谢谢", "辛苦",
        "晚安", "早安", "抱抱", "摸摸", "贴贴", "乖", "宝贝", "爱了", "哈哈哈", "白米饭", "好吃", "请你吃",
    ];

    private static readonly string[] DownWords =
    [
        "讨厌", "滚", "烦死", "闭嘴", "笨", "蠢", "傻", "丑", "垃圾", "废物", "恶心", "无聊",
        "走开", "别理我", "不理你", "别说话", "生气", "难过", "烦人",
    ];

    /// <summary>主动搭话的由头池（原 companion.js:168-175）。</summary>
    private static readonly string[] Angles =
    [
        "把刚在网上看到的有趣东西分享给他",
        "问问他现在在干嘛",
        "撒个娇或者小小地吐槽一下",
        "发个表情包或者来句感慨",
        "应景说点什么（吃饭了没、天气、时间点）",
        "问问他手头的事进展怎样",
    ];

    private readonly FeatureHost _host;
    private readonly GapPool _gaps;
    private readonly string _file;
    private readonly Lock _gate = new();

    private JsonObject _state = NewState(Hormones.Base, "");

    public MoodService(FeatureHost host, GapPool gaps)
    {
        _host = host;
        _gaps = gaps;
        _file = host.Store.PathIn("mood.json");
        Load();
    }

    private bool Enabled => _host.Flag(true, "mood", "enabled");
    private bool JudgeEnabled => _host.Flag(true, "mood", "judge");
    private bool DecayEnabled => _host.Flag(true, "mood", "decay");
    private AreaLog Log => _host.Log.Child("mood");

    private static JsonObject NewState(Hormones h, string reason) => new()
    {
        ["dopamine"] = h.Dopamine,
        ["cortisol"] = h.Cortisol,
        ["serotonin"] = h.Serotonin,
        ["at"] = NowMs(),
        ["reason"] = reason,
        ["history"] = new JsonArray(),
    };

    private void Load()
    {
        lock (_gate)
        {
            var saved = DataStore.ReadObject(_file, new JsonObject());
            if (saved.Count == 0) return;

            // 旧版只有一个 value（0~100 好感度），按原 companion.js:49-61 迁移。
            if (saved["dopamine"] is not JsonNode)
            {
                var value = JsonUtil.Num(saved["value"], 60);
                var off = value - 60;
                _state = NewState(new Hormones(
                    Math.Round(HormoneMath.Clamp(50 + off * 1.4, 0, 100)),
                    Math.Round(HormoneMath.Clamp(20 - off * 0.8, 0, 100)),
                    Math.Round(HormoneMath.Clamp(50 + off * 0.9, 0, 100))), "旧数据迁移");
            }
            else
            {
                _state = NewState(new Hormones(
                    JsonUtil.Num(saved["dopamine"], 50),
                    JsonUtil.Num(saved["cortisol"], 20),
                    JsonUtil.Num(saved["serotonin"], 50)),
                    JsonUtil.Str(saved["reason"], ""));
                // DeepClone：hist 还挂在 saved 上，直接赋给 _state 会触发「node already has a parent」
                if (saved["history"] is JsonArray hist) _state["history"] = hist.DeepClone();
            }
        }
    }

    private void Save(Hormones from, Hormones to, string reason)
    {
        lock (_gate)
        {
            _state["dopamine"] = to.Dopamine;
            _state["cortisol"] = to.Cortisol;
            _state["serotonin"] = to.Serotonin;
            _state["at"] = NowMs();
            _state["reason"] = reason;
            var hist = _state["history"] as JsonArray ?? new JsonArray();
            hist.Add(new JsonObject
            {
                ["at"] = NowMs(),
                ["from"] = new JsonObject { ["d"] = from.Dopamine, ["c"] = from.Cortisol, ["s"] = from.Serotonin },
                ["to"] = new JsonObject { ["d"] = to.Dopamine, ["c"] = to.Cortisol, ["s"] = to.Serotonin },
                ["reason"] = reason,
            });
            while (hist.Count > 60) hist.RemoveAt(0);
            _state["history"] = hist;
            DataStore.WriteJson(_file, _state);
            // 双源同步：读优先 life（state.json），写也同步回 life，保证对称
            // 在锁外调用 UpdateHormones 可避免潜在的 lock 顺序问题；to 是值类型（record struct），副本安全
            var hormonesToSync = to;
            var reasonToSync = reason;
            try
            {
                _host.Get<LifeStateService>()?.UpdateHormones(hormonesToSync, reasonToSync);
            }
            catch (Exception syncEx)
            {
                // 同步失败不影响主流程；mood.json 已落盘
                Log.Warn($"同步激素到 state.json 失败：{syncEx.Message}");
            }
        }
    }

    /// <summary>把落盘值按半衰期衰减到「现在」；mood.decay=false 时不衰减。优先从 LifeStateService 读取。</summary>
    private Hormones Current()
    {
        // 优先从 LifeStateService 读取（如果已注册且启用）
        var lifeState = _host.Get<LifeStateService>();
        if (lifeState != null)
        {
            try
            {
                return lifeState.GetCurrentHormones();
            }
            catch (Exception ex)
            {
                Log.Warn($"从 LifeStateService 读取激素失败，降级到 mood.json: {ex.Message}");
            }
        }
        
        // 降级到原实现（向后兼容，支持独立运行）
        Hormones saved;
        long at;
        lock (_gate)
        {
            saved = new Hormones(
                JsonUtil.Num(_state["dopamine"], 50),
                JsonUtil.Num(_state["cortisol"], 20),
                JsonUtil.Num(_state["serotonin"], 50));
            // at 是毫秒时间戳，超出 int32：必须按 long 读，溢出会把衰减算成几万天、三轴直接回基线
            at = (long)JsonUtil.Num(_state["at"], NowMs());
        }
        if (!DecayEnabled) return saved;
        return HormoneMath.Decayed(saved, at, NowMs());
    }

    public Task<double> TemperatureAsync(double basetemp, CancellationToken ct = default)
    {
        if (!Enabled) return Task.FromResult(basetemp);
        var hijack = HormoneMath.HijackTemperature(Current());
        return Task.FromResult(hijack ?? basetemp);
    }

    public double StickerFactor() => Enabled ? HormoneMath.DeriveEffects(Current()).Sticker : 1.0;

    public double DelayFactor() => Enabled ? HormoneMath.DeriveEffects(Current()).Delay : 1.0;

    public string ContextLine()
    {
        if (!Enabled) return "";
        var directives = HormoneMath.DeriveDirectives(Current());
        return directives.Count == 0 ? "" : $"（{string.Join(" ", directives)}）";
    }

    /// <summary>每轮对话后观察情绪：judge 开着用模型判，否则用词表兜底（原 companion.js:108-150）。</summary>
    public async Task AfterExchangeAsync(string? userText, string? botText, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(userText)) return;
        try
        {
            if (JudgeEnabled)
                await JudgeAsync(userText, botText, ct);
            else
                ObserveByWords(userText);
        }
        catch (Exception ex)
        {
            Log.Warn("观察情绪失败：", ex.Message);
        }
    }

    private static (int Up, int Down) CountWords(string? text)
    {
        text ??= "";
        var up = 0;
        var down = 0;
        foreach (var w in UpWords) up += CountOccurrences(text, w);
        foreach (var w in DownWords) down += CountOccurrences(text, w);
        return (up, down);
    }

    private static int CountOccurrences(string text, string word)
    {
        var n = 0;
        var idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0) { n++; idx += word.Length; }
        return n;
    }

    /// <summary>词表打分（原 scoreText）：无命中返回 null。</summary>
    private static Hormones? ScoreText(string? text)
    {
        var (up, down) = CountWords(text);
        if (up == 0 && down == 0) return null;
        var d = Math.Min(18, up * 5) - down * 3;
        var c = down * 7 - up * 2;
        var s = up * 4 - down * 6;
        return new Hormones(d, c, s);
    }

    /// <summary>词表兜底（原 observe）：k = (direct?1:0.3) * (owner?1.4:1)，这里只会收到主人直接发言 → k=1.4。</summary>
    private void ObserveByWords(string userText)
    {
        var deltas = ScoreText(userText);
        if (deltas is null) return;
        var k = 1.4;
        var cur = Current();
        var next = HormoneMath.ApplyDeltas(cur, new Hormones(
            Math.Round(deltas.Value.Dopamine * k),
            Math.Round(deltas.Value.Cortisol * k),
            Math.Round(deltas.Value.Serotonin * k)));
        Save(cur, next, "词表观察");
    }

    /// <summary>让模型判这轮对话对她情绪的影响（原 judge），confidence≥0.6 且非全 0 才落盘。</summary>
    private async Task JudgeAsync(string userText, string? botText, CancellationToken ct)
    {
        var outText = await MediaUtil.AskModelAsync(
            _host,
            "你判断一段对话对「她」情绪的影响。她是有感情的 AI 伴侣。只输出 JSON："
            + "{\"d\":-25到25的整数,\"c\":-25到25的整数,\"s\":-25到25的整数,\"reason\":\"不超过40字\",\"confidence\":0到1}。"
            + " d=多巴胺（开心/兴致），c=皮质醇（压力/紧张），s=血清素（安心/自我肯定）。"
            + "被夸、被喜欢、聊得开心 → d正c负s正；被骂、被晾着、被提无理要求 → d负c正s负。拿不准就给低 confidence。",
            $"她刚说完：{MediaUtil.Slice(botText, 160)}\n对方说：{MediaUtil.Slice(userText, 300)}",
            0.2, 200, 30_000, ct);
        if (string.IsNullOrWhiteSpace(outText)) return;
        var obj = MediaUtil.ExtractJsonObject(outText);
        if (obj is null) return;

        var d = (int)Math.Round(Math.Clamp(JsonUtil.Num(obj["d"], 0), -25, 25));
        var c = (int)Math.Round(Math.Clamp(JsonUtil.Num(obj["c"], 0), -25, 25));
        var s = (int)Math.Round(Math.Clamp(JsonUtil.Num(obj["s"], 0), -25, 25));
        var confidence = JsonUtil.Num(obj["confidence"], 0);
        var reason = MediaUtil.Slice(JsonUtil.Str(obj["reason"], ""), 40);
        if (confidence < 0.6 || (d == 0 && c == 0 && s == 0)) return;

        var cur = Current();
        var next = HormoneMath.ApplyDeltas(cur, new Hormones(d, c, s));
        Save(cur, next, string.IsNullOrWhiteSpace(reason) ? "对话影响" : reason);
    }

    /// <summary>主动搭话的由头：有未解决的 gap 优先说那件事，否则从由头池随机挑一个。</summary>
    public Task<string?> SpeakUpCueAsync(CancellationToken ct = default)
    {
        if (_gaps.Enabled)
        {
            var top = _gaps.Top(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (top is not null)
            {
                var text = JsonUtil.Str(top["text"], "");
                if (!string.IsNullOrWhiteSpace(text))
                    return Task.FromResult<string?>($"接着你心里挂着的那件事说：{text}");
            }
        }
        var angle = Angles[Random.Shared.Next(Angles.Length)];
        return Task.FromResult<string?>(angle);
    }

    public JsonObject State()
    {
        var h = Current();
        lock (_gate)
        {
            var obj = JsonNode.Parse(_state.ToJsonString()) as JsonObject ?? new JsonObject();
            obj["dopamine"] = (int)h.Dopamine;
            obj["cortisol"] = (int)h.Cortisol;
            obj["serotonin"] = (int)h.Serotonin;
            obj["value"] = HormoneMath.SummaryValue(h);
            obj["enabled"] = Enabled;
            return obj;
        }
    }

    public JsonNode? Snapshot() => State();

    public JsonArray History()
    {
        lock (_gate)
        {
            return _state["history"] is JsonArray h
                ? JsonNode.Parse(h.ToJsonString()) as JsonArray ?? new JsonArray()
                : new JsonArray();
        }
    }

    public void Reset()
    {
        var from = Current();
        Save(from, Hormones.Base, "手动重置");
    }

    /// <summary>手动微调：只动 dopamine（面板滑条用），夹 ±50。</summary>
    public void Nudge(double delta, string reason)
    {
        delta = Math.Clamp(delta, -50, 50);
        var cur = Current();
        var next = HormoneMath.ApplyDeltas(cur, new Hormones(Math.Round(delta), 0, 0));
        Save(cur, next, string.IsNullOrWhiteSpace(reason) ? "手动调整" : reason);
    }

    internal static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
