using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Core.Config;

/// <summary>
/// 配置中心。数据形状与默认值逐字对齐原 Node 版 core/config.js，
/// 这样网页端 publicConfig() 拿到的 JSON 结构完全一致，前端不用改。
/// </summary>
public sealed class ConfigStore(DataStore store, AppLog log)
{
    private const string DefaultsJson = """
{
  "enabled": true,
  "setupDone": false,
  "ownerName": "主人",
  "botName": "大肥鱼",
  "botTag": "",
  "siteName": "",
  "pageTitle": "",
  "web": { "host": "0.0.0.0", "port": 2332 },
  "model": { "provider": "", "model": "", "reasoningEffort": "low", "baseUrl": "", "apiKey": "", "temperature": 1.0, "maxTokens": 1024, "timeoutMs": 120000 },
  "merge": { "enabled": true, "waitSec": 2.5, "maxSec": 8, "waitTypingSec": 5 },
  "sound": {
    "onIncoming": true, "onSend": true,
    "incoming": "chime", "outgoing": "bubble",
    "incomingUrl": "", "outgoingUrl": "",
    "inVolume": 0.5, "outVolume": 0.5
  },
  "logs": { "maxMB": 2 },
  "theme": { "mode": "follow", "background": "" },
  "imageGen": {
    "enabled": false,
    "provider": "openai",
    "baseUrl": "",
    "apiKey": "",
    "model": "",
    "size": "1024x1024",
    "stylePrompt": "",
    "steps": 25,
    "cfgScale": 7,
    "sampler": "DPM++ 2M Karras",
    "negativePrompt": "",
    "ownerOnly": true
  },
  "persona": { "current": "dafeiyu" },
  "voice": {
    "enabled": false,
    "provider": "auto",
    "ttsBaseUrl": "",
    "ttsApiKey": "",
    "ttsModel": "tts-1",
    "ttsVoice": "alloy",
    "engine": "auto",
    "cosyvoice": { "url": "http://127.0.0.1:50000", "mode": "sft", "spkId": "中文女", "instruct": "", "sampleRate": 22050 },
    "gptsovits": { "url": "http://127.0.0.1:9880", "refAudio": "", "promptText": "", "promptLang": "zh", "textLang": "zh" },
    "http": { "urlTemplate": "" },
    "voice": "zh-CN-XiaoyiNeural",
    "rate": "+0%",
    "pitch": "+0Hz",
    "autoChance": 0.15,
    "autoMaxChars": 80,
    "asr": false,
    "asrLanguage": "zh"
  },
  "stickers": {
    "enabled": true,
    "autoChance": 0.35,
    "maxPerHour": 20,
    "maxLibrary": 300,
    "caption": true,
    "steal": true,
    "selfDraw": true,
    "drawEvery": 3
  },
  "diary": {
    "enabled": true,
    "timesPerDay": 2,
    "windowStart": "10:00",
    "windowEnd": "23:30",
    "minGapMinutes": 180,
    "minChars": 60,
    "maxChars": 140,
    "visibility": "all"
  },
  "local": { "tts": false, "image": false, "song": false, "asr": false },
  "azoneSocial": { "enabled": true, "replyOwn": true, "maxPerDay": 8 },
  "speakUp": {
    "enabled": true,
    "idleMinutes": 60,
    "minGapMinutes": 120,
    "maxPerDay": 4,
    "quietFrom": "23:00",
    "quietTo": "08:00"
  },
  "mood": {
    "enabled": true,
    "halfLifeHours": 3,
    "neutralHigh": 60,
    "neutralLow": 50,
    "judge": true,
    "decay": true
  },
  "guard": {
    "enabled": false,
    "baseUrl": "",
    "apiKey": "",
    "model": "guard",
    "timeoutMs": 4000,
    "threshold": 0.6,
    "topLogprobs": 25
  },
  "memory": {
    "enabled": true,
    "recallCount": 6,
    "dailyConsolidate": true,
    "consolidateTimes": ["04:00", "16:00"],
    "compressEveryHours": 6,
    "gradeFull": 0.7,
    "gradeBrief": 0.3,
    "gradeDrop": 0.05
  },
  "gaps": {
    "enabled": true,
    "baseTension": 80,
    "tensionPerHour": 5,
    "maxTension": 200,
    "dedupeThreshold": 0.6,
    "dedupeBump": 10,
    "maxItems": 60
  },
  "history": { "keepDays": 7 },
  "selfDrive": {
    "enabled": false,
    "intervalMinutes": 60,
    "maxPerDay": 4,
    "quietFrom": "01:00",
    "quietTo": "08:00"
  },
  "tabs": {}
}
""";

    /// <summary>对外隐藏的密钥字段：group → key。publicConfig 会把值清空并给 xxxSet 布尔。</summary>
    private static readonly (string Group, string Key)[] Secrets = [("model", "apiKey"), ("imageGen", "apiKey"), ("voice", "ttsApiKey")];

    private static readonly string[] ReasoningEfforts = ["off", "low", "medium", "high"];
    private static readonly string[] ThemeModes = ["follow", "light", "dark", "acrylic", "frosted"];
    private static readonly string[] Tones = ["ding", "pop", "bubble", "chime", "custom", "none"];
    private static readonly string[] VoiceProviders = ["auto", "openai", "edge", "windows", "local", "cosyvoice", "gptsovits", "http"];
    private static readonly string[] VoiceEngines = ["auto", "edge", "windows", "cosyvoice", "gptsovits", "http"];
    private static readonly string[] ImageProviders = ["openai", "sdwebui", "comfyui", "local", "code"];

    private readonly string _file = store.PathIn("config.json");
    private JsonObject _current = null!;

    public JsonObject Current => _current;

    public ConfigStore Load()
    {
        _current = Normalize(DataStore.ReadObject(_file));
        return this;
    }

    public JsonObject Save(JsonObject cfg)
    {
        _current = Normalize(cfg);
        DataStore.WriteJson(_file, _current);
        log.Child("core").Info("配置已保存");
        return _current;
    }

    /// <summary>给网页/客户端看的配置：密钥清空，只给 xxxSet。</summary>
    public JsonObject PublicView()
    {
        var c = (JsonObject)_current.DeepClone();
        foreach (var (group, key) in Secrets)
        {
            if (c[group] is not JsonObject g) continue;
            g[key + "Set"] = bool.TryParse(g[key]?.ToString(), out var set) ? set : !string.IsNullOrEmpty(g[key]?.ToString());
            g[key] = "";
        }
        return c;
    }

    /// <summary>面板改配置：深合并 → 密钥为 null 表示清空、空值表示保持原值。</summary>
    public JsonObject MergeFromPanel(JsonObject incoming)
    {
        var next = DeepMerge(_current, incoming);
        foreach (var (group, key) in Secrets)
        {
            if (next[group] is not JsonObject g) continue;
            var v = (incoming[group] as JsonObject)?[key];
            if (v is null && (incoming[group] as JsonObject)?.ContainsKey(key) == true) g[key] = "";
            else if (v is null || string.IsNullOrEmpty(v.ToString())) g[key] = _current[group]?[key]?.DeepClone();
            g.Remove(key + "Set");
        }
        return Save(next);
    }

    public static JsonObject Defaults() => JsonNode.Parse(DefaultsJson)!.AsObject();

    // ---------- 归一化：逐条对齐 core/config.js 的 normalize() ----------

    public static JsonObject Normalize(JsonObject? raw)
    {
        var c = DeepMerge(Defaults(), raw ?? new JsonObject());

        c["enabled"] = Bool(c["enabled"], true);
        c["setupDone"] = Bool(c["setupDone"], false);
        c["ownerName"] = Str(c["ownerName"], "主人");
        c["botName"] = Str(c["botName"], "大肥鱼");
        c["botTag"] = Str(c["botTag"], "");
        c["siteName"] = Cut(Str(c["siteName"], ""), 40);
        c["pageTitle"] = Cut(Str(c["pageTitle"], ""), 60);

        var web = Sub(c, "web");
        web["port"] = (int)Num(web["port"], 2332, 1, 65535);
        web["host"] = Str(web["host"], "0.0.0.0");

        var model = Sub(c, "model");
        if (!ReasoningEfforts.Contains(Str(model["reasoningEffort"], "low"))) model["reasoningEffort"] = "low";

        var theme = Sub(c, "theme");
        if (!ThemeModes.Contains(Str(theme["mode"], "follow"))) theme["mode"] = "follow";
        theme["background"] = Str(theme["background"], "");

        var merge = Sub(c, "merge");
        merge["enabled"] = Bool(merge["enabled"], true);
        merge["waitSec"] = Num(merge["waitSec"], 2.5, 0, 30);
        merge["maxSec"] = Math.Max(merge["waitSec"]!.GetValue<double>(), Num(merge["maxSec"], 8, 0, 60));
        merge["waitTypingSec"] = Num(merge["waitTypingSec"], 5, 0, 60);

        var snd = Sub(c, "sound");
        snd["onIncoming"] = Bool(snd["onIncoming"], true);
        snd["onSend"] = Bool(snd["onSend"], false);
        snd["inVolume"] = Num(snd["inVolume"] ?? snd["volume"], 0.5, 0, 1);
        snd["outVolume"] = Num(snd["outVolume"] ?? snd["volume"], 0.5, 0, 1);
        snd.Remove("volume");
        if (!Tones.Contains(Str(snd["incoming"], "chime"))) snd["incoming"] = "ding";
        if (!Tones.Contains(Str(snd["outgoing"], "bubble"))) snd["outgoing"] = "pop";
        snd["incomingUrl"] = Cut(Str(snd["incomingUrl"], ""), 500);
        snd["outgoingUrl"] = Cut(Str(snd["outgoingUrl"], ""), 500);

        if (c["logs"] is not JsonObject) c["logs"] = new JsonObject { ["maxMB"] = 2 };
        Sub(c, "logs")["maxMB"] = Num(Sub(c, "logs")["maxMB"], 2, 0.2, 50);

        var voice = Sub(c, "voice");
        voice["enabled"] = Bool(voice["enabled"], false);
        voice["asr"] = Bool(voice["asr"], false);
        if (!VoiceProviders.Contains(Str(voice["provider"], "auto"))) voice["provider"] = "auto";
        voice["ttsBaseUrl"] = Str(voice["ttsBaseUrl"], "");
        voice["ttsModel"] = Str(voice["ttsModel"], "tts-1");
        voice["ttsVoice"] = Str(voice["ttsVoice"], "alloy");
        voice.Remove("asrProvider");
        voice.Remove("asrBaseUrl");
        voice.Remove("asrApiKey");
        voice.Remove("asrModel");
        voice["autoChance"] = Num(voice["autoChance"], 0.15, 0, 1);
        voice["autoMaxChars"] = Num(voice["autoMaxChars"], 80, 20, 300);
        if (!VoiceEngines.Contains(Str(voice["engine"], "auto"))) voice["engine"] = "auto";
        var cosy = Sub(voice, "cosyvoice");
        cosy["sampleRate"] = Num(cosy["sampleRate"], 22050, 8000, 48000);
        var mode = Str(cosy["mode"], "sft");
        if (mode != "sft" && mode != "instruct") cosy["mode"] = "sft";

        var stickers = Sub(c, "stickers");
        stickers["autoChance"] = Num(stickers["autoChance"], 0.35, 0.1, 1);
        stickers["maxPerHour"] = Num(stickers["maxPerHour"], 20, 0, 200);
        stickers["maxLibrary"] = Num(stickers["maxLibrary"], 300, 20, 2000);
        stickers["steal"] = Bool(stickers["steal"], true);

        var diary = Sub(c, "diary");
        diary["timesPerDay"] = Num(diary["timesPerDay"], 2, 0, 6);
        diary["windowStart"] = Hhmm(diary["windowStart"], "10:00");
        diary["windowEnd"] = Hhmm(diary["windowEnd"], "23:30");
        diary["minGapMinutes"] = Num(diary["minGapMinutes"], 180, 0, 1440);
        diary["minChars"] = Num(diary["minChars"], 60, 10, 500);
        diary["maxChars"] = Num(diary["maxChars"], 140, 20, 3000);
        diary["visibility"] = Str(diary["visibility"], "all") == "self" ? "self" : "all";

        var local = Sub(c, "local");
        foreach (var k in new[] { "tts", "image", "song", "asr" }) local[k] = Bool(local[k], false);

        var q = Sub(c, "azoneSocial");
        q["enabled"] = Bool(q["enabled"], true);
        q["replyOwn"] = Bool(q["replyOwn"], true);
        q["maxPerDay"] = Num(q["maxPerDay"], 8, 0, 50);

        var su = Sub(c, "speakUp");
        su["enabled"] = Bool(su["enabled"], true);
        su["idleMinutes"] = Num(su["idleMinutes"], 60, 1, 1440);
        su["minGapMinutes"] = Num(su["minGapMinutes"], 120, 0, 2880);
        su["maxPerDay"] = Num(su["maxPerDay"], 4, 0, 20);
        su["quietFrom"] = Hhmm(su["quietFrom"], "23:00");
        su["quietTo"] = Hhmm(su["quietTo"], "08:00");
        foreach (var k in new[] { "mode", "intervalMinutes", "timesPerDay", "nudge", "minIntervalMinutes", "maxIntervalMinutes", "awakeFrom", "awakeTo" })
            su.Remove(k);

        var mo = Sub(c, "mood");
        mo["enabled"] = Bool(mo["enabled"], true);
        mo["halfLifeHours"] = Num(mo["halfLifeHours"], 3, 0.25, 72);
        mo["neutralHigh"] = Num(mo["neutralHigh"], 60, 1, 100);
        mo["neutralLow"] = Num(mo["neutralLow"], 50, 0, 100);
        if (mo["neutralLow"]!.GetValue<double>() > mo["neutralHigh"]!.GetValue<double>())
            mo["neutralLow"] = mo["neutralHigh"]!.DeepClone();
        mo["judge"] = Bool(mo["judge"], true);
        mo["decay"] = Bool(mo["decay"], true);

        if (c["guard"] is not JsonObject) c["guard"] = new JsonObject();
        var gd = Sub(c, "guard");
        gd["enabled"] = Bool(gd["enabled"], false);
        gd["baseUrl"] = Str(gd["baseUrl"], "").Trim();
        gd["apiKey"] = Str(gd["apiKey"], "");
        gd["model"] = Str(gd["model"], "guard");
        gd["timeoutMs"] = Num(gd["timeoutMs"], 4000, 500, 30000);
        gd["threshold"] = Num(gd["threshold"], 0.6, 0, 20);
        gd["topLogprobs"] = Num(gd["topLogprobs"], 25, 1, 50);

        var sd = Sub(c, "selfDrive");
        sd["enabled"] = Bool(sd["enabled"], false);
        sd["intervalMinutes"] = Num(sd["intervalMinutes"], 60, 5, 1440);
        sd["maxPerDay"] = Num(sd["maxPerDay"], 4, 0, 50);
        sd["quietFrom"] = Hhmm(sd["quietFrom"], "01:00");
        sd["quietTo"] = Hhmm(sd["quietTo"], "08:00");

        var mem = Sub(c, "memory");
        mem["consolidateTimes"] = NormalizeTimes(mem["consolidateTimes"]);
        mem["recallCount"] = Num(mem["recallCount"], 6, 0, 20);
        mem["compressEveryHours"] = Num(mem["compressEveryHours"], 6, 1, 168);
        mem["gradeFull"] = Num(mem["gradeFull"], 0.7, 0.05, 0.99);
        mem["gradeBrief"] = Num(mem["gradeBrief"], 0.3, 0.01, 0.95);
        mem["gradeDrop"] = Num(mem["gradeDrop"], 0.05, 0, 0.5);
        if (mem["gradeBrief"]!.GetValue<double>() > mem["gradeFull"]!.GetValue<double>())
            mem["gradeBrief"] = mem["gradeFull"]!.DeepClone();
        if (mem["gradeDrop"]!.GetValue<double>() > mem["gradeBrief"]!.GetValue<double>())
            mem["gradeDrop"] = mem["gradeBrief"]!.DeepClone();

        if (c["history"] is not JsonObject) c["history"] = new JsonObject { ["keepDays"] = 7 };
        Sub(c, "history")["keepDays"] = Num(Sub(c, "history")["keepDays"], 7, 1, 365);

        if (c["gaps"] is not JsonObject) c["gaps"] = new JsonObject();
        var gaps = Sub(c, "gaps");
        gaps["enabled"] = Bool(gaps["enabled"], true);
        gaps["baseTension"] = Num(gaps["baseTension"], 80, 10, 500);
        gaps["tensionPerHour"] = Num(gaps["tensionPerHour"], 5, 0, 100);
        gaps["maxTension"] = Num(gaps["maxTension"], 200, 50, 1000);
        gaps["dedupeThreshold"] = Num(gaps["dedupeThreshold"], 0.6, 0.1, 1);
        gaps["dedupeBump"] = Num(gaps["dedupeBump"], 10, 0, 100);
        gaps["maxItems"] = Num(gaps["maxItems"], 60, 5, 500);

        var img = Sub(c, "imageGen");
        img["enabled"] = Bool(img["enabled"], false);
        img["ownerOnly"] = Bool(img["ownerOnly"], true);
        if (!ImageProviders.Contains(Str(img["provider"], "openai"))) img["provider"] = "openai";
        if (!Regex.IsMatch(Str(img["size"], ""), @"^\d{3,4}x\d{3,4}$")) img["size"] = "1024x1024";
        img["steps"] = Num(img["steps"], 25, 1, 150);
        img["cfgScale"] = Num(img["cfgScale"], 7, 0, 30);
        img["negativePrompt"] = Str(img["negativePrompt"], "");

        return c;
    }

    // ---------- 按路径读配置（Core/Features 共用；Server 的 ApiContext 另有同名助手） ----------

    /// <summary>按路径取值，如 Pick("web", "port")。</summary>
    public JsonNode? Pick(params string[] path)
    {
        JsonNode? cur = _current;
        foreach (var key in path)
        {
            if (cur is not JsonObject obj) return null;
            cur = obj[key];
        }
        return cur;
    }

    /// <summary>按路径取字符串，空则用 fallback。</summary>
    public string Text(string fallback, params string[] path)
        => Str(Pick(path), fallback);

    /// <summary>按路径取开关，非法则用 fallback。</summary>
    public bool On(bool fallback, params string[] path)
        => Bool(Pick(path), fallback);

    // ---------- 小工具 ----------

    private static JsonObject Sub(JsonObject o, string key)
    {
        if (o[key] is JsonObject sub) return sub;
        var created = new JsonObject();
        o[key] = created;
        return created;
    }

    private static JsonObject DeepMerge(JsonObject baseObj, JsonObject over)
    {
        var result = (JsonObject)baseObj.DeepClone();
        foreach (var (key, value) in over)
        {
            if (value is JsonObject child && result[key] is JsonObject existing) result[key] = DeepMerge(existing, child);
            else if (value is not null) result[key] = value.DeepClone();
        }
        return result;
    }

    private static double Num(JsonNode? v, double fallback, double lo, double hi)
    {
        if (v is null) return fallback;
        if (!double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n)) return fallback;
        return Math.Min(hi, Math.Max(lo, n));
    }

    private static bool Bool(JsonNode? v, bool fallback)
    {
        if (v is null) return fallback;
        var s = v.ToString();
        if (s is "true" or "false") return s == "true";
        return fallback;
    }

    private static string Str(JsonNode? v, string fallback)
    {
        var s = v?.ToString();
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Hhmm(JsonNode? v, string fallback)
    {
        var s = v?.ToString() ?? "";
        return Regex.IsMatch(s, @"^\d{1,2}:\d{2}$") ? s : fallback;
    }

    private static JsonArray NormalizeTimes(JsonNode? v)
    {
        IEnumerable<string> raw = v switch
        {
            JsonArray arr => arr.Select(x => x?.ToString() ?? ""),
            null => [],
            _ => Regex.Split(v.ToString() ?? "", @"[\s,，]+"),
        };
        var list = raw
            .Select(t => Hhmm(JsonValue.Create(t), ""))
            .Where(t => t.Length > 0)
            .Select(t => t.PadLeft(5, '0'))
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (list.Length == 0) list = ["04:00", "16:00"];
        return new JsonArray(list.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
    }
}
