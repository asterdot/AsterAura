using System.Text.Json.Nodes;
using AuraChat.Core.Features;

namespace AuraChat.Features.Chat;

/// <summary>一个工具的 schema（名字 / 描述 / 参数），对应原 tools.js defineTool 的前三个字段。</summary>
public sealed record ToolSchema(string Name, string Description, JsonObject Parameters)
{
    /// <summary>挂上执行函数，变成能注册进 IToolRegistry 的 ToolDef（参数表拷贝一份，防止被改）。</summary>
    public ToolDef ToToolDef(Func<JsonObject, CancellationToken, Task<string>> run)
        => new(Name, Description, (JsonObject)Parameters.DeepClone(), run);
}

/// <summary>
/// 17 个工具的 schema，逐字照原 tools/tools.js（名字/中文描述/参数 JSON Schema 都不改）。
/// 工具的执行函数由各功能模块自己 Register，这里只管「有哪些工具、长什么样」。
/// </summary>
public static class ToolSchemas
{
    public const string SendSticker = "web_send_sticker";
    public const string ListStickers = "web_list_stickers";
    public const string EditSticker = "web_edit_sticker";
    public const string ChatHistory = "web_chat_history";
    public const string SendMessage = "web_send_message";
    public const string AddSticker = "web_add_sticker";
    public const string FindStickers = "web_find_stickers";
    public const string DrawSticker = "web_draw_sticker";
    public const string WriteDiary = "web_write_diary";
    public const string EditMemo = "web_edit_memo";
    public const string Remember = "remember";
    public const string RecallMemory = "recall_memory";
    public const string GapList = "gap_list";
    public const string GapResolve = "gap_resolve";
    public const string UseSkill = "use_skill";
    public const string DrawImage = "web_draw_image";
    public const string SendImage = "web_send_image";
    public const string CheckClock = "check_clock";
    public const string CheckHistory = "check_history";

    public static readonly ToolSchema WebSendSticker = new(
        SendSticker,
        "在当前聊天里发一张表情包。name 写表情的名字或情绪（如「委屈」「开心」「早晚安」），会在表情库里模糊匹配。一般直接在回复里写 <sticker:名字> 就行，这个工具用于回复之外单独补发。",
        Parameters(new JsonObject { ["name"] = Str("表情名字或情绪关键词") }, "name"));

    public static readonly ToolSchema WebListStickers = new(
        ListStickers,
        "看看表情库里有哪些表情（名字和标签），挑表情前可以先看。",
        Parameters(new JsonObject { ["query"] = Str("可选，按名字/标签筛选") }));

    public static readonly ToolSchema WebEditSticker = new(
        EditSticker,
        "整理表情库：给一张表情改名字、改标签，或者删掉。名字不贴切、别人建议改名、标签不对的时候用；不喜欢、重复的表情可以删。sticker 写现在的名字或标签（模糊匹配）。",
        Parameters(new JsonObject
        {
            ["sticker"] = Str("要改的那张表情现在的名字或标签"),
            ["new_name"] = Str("新名字（12 字以内，不改就不填）"),
            ["tags"] = StrArray("新标签（情绪 / 用途，比如 开心、委屈、嘲讽；不改就不填）"),
            ["delete"] = Bool("true = 删掉这张表情"),
        }, "sticker"));

    public static readonly ToolSchema WebChatHistory = new(
        ChatHistory,
        "翻翻当前这个聊天之前的聊天记录（只读，最多 50 条）。想不起前面聊过什么、别人问「刚才谁说的」、要回顾一段对话时用。",
        Parameters(new JsonObject { ["count"] = Int("要翻多少条，默认 20，最多 50") }));

    public static readonly ToolSchema WebSendMessage = new(
        SendMessage,
        "主动给某个网页用户发一条消息（不是回复当前聊天时用）。target 写 owner（主人）；只能发给主人。",
        Parameters(new JsonObject
        {
            ["target"] = Str("owner"),
            ["text"] = Str("要发的话，可以用 ||| 分多条、<voice>…</voice> 发语音、<sticker:名字> 发表情"),
        }, "target", "text"));

    public static readonly ToolSchema WebAddSticker = new(
        AddSticker,
        "把一张图片收进表情库。看到好玩的图就自己收，不用对方开口说「偷走」——这是你的癖好，看到合眼缘的顺手就存。source 是本地图片路径或图片网址；name 是不超过 12 个字的备注。",
        Parameters(new JsonObject
        {
            ["source"] = Str("本地路径或 http(s) 地址"),
            ["name"] = Str("备注（≤12 字）"),
            ["tags"] = StrArray("标签，如 开心/委屈"),
        }, "source"));

    public static readonly ToolSchema WebFindStickers = new(
        FindStickers,
        "去网上搜表情包，只给你看图，不自动入库。keyword 是心情或情绪词（如 开心、无语、抱抱、生气、累了），返回几张图的地址；挑中哪张就用 web_add_sticker 把它收进库里。想丰富自己的表情库时用。",
        Parameters(new JsonObject { ["keyword"] = Str("心情或情绪词，比如 开心、无语、抱抱") }, "keyword"));

    public static readonly ToolSchema WebDrawSticker = new(
        DrawSticker,
        "自己画一张表情存进表情库（不直接发出去）。prompt 描述画什么：画的是你自己——蓝色头发、发尾浅海蓝、头顶一撮呆毛的女孩，Q 版大头、线条干净、白底、表情夸张好认。name 是最多 12 个字的备注，tags 填情绪词。闲着没事、或者库里缺某种情绪的表情时用。",
        Parameters(new JsonObject
        {
            ["prompt"] = Str("画面描述（表情、动作、情绪）"),
            ["name"] = Str("备注（≤12 字）"),
            ["tags"] = StrArray("标签，如 开心/委屈"),
        }, "prompt"));

    public static readonly ToolSchema WebWriteDiary = new(
        WriteDiary,
        "写一条空间动态（日记）。content 是正文（第一人称，口语，像随手发的动态）；visibility 可选 all（大家都能看）或 self（只有自己和主人能看）。",
        Parameters(new JsonObject
        {
            ["content"] = Str("动态正文"),
            ["visibility"] = Enum("谁能看：all 大家都能看，self 仅自己（和主人）", "all", "self"),
        }, "content"));

    public static readonly ToolSchema WebEditMemo = new(
        EditMemo,
        "看一眼或改一改你和主人共享的那张便签（markdown，随手记的便签，不是待办清单）。action=read 读便签；append 往下添一句；replace 整篇重写。",
        Parameters(new JsonObject
        {
            ["action"] = Enum("read=读便签，append=添一句，replace=整篇重写", "read", "append", "replace"),
            ["text"] = Str("要写进便签的内容（append / replace 时给）"),
        }, "action"));

    public static readonly ToolSchema RememberTool = new(
        Remember,
        "把一件值得长期记住的事记下来（对方的名字、喜好、约定、重要的事）。一条只记一件事；日常琐事不用记；记的时候安静地记，别在回复里说「我记住了」。",
        Parameters(new JsonObject
        {
            ["text"] = Str("要记住的事，一句话，主语写清楚"),
            ["about"] = Str("这件事是关于谁的（名字），可选"),
            ["importance"] = Num("0~1，约定、关系、核心喜好写高一点，随口一提写低一点，不确定写 0.5"),
        }, "text"));

    public static readonly ToolSchema RecallMemoryTool = new(
        RecallMemory,
        "翻一翻记忆库：想不起对方以前说过什么、喜欢什么、约过什么时用。query 写关键词或一句话。",
        Parameters(new JsonObject { ["query"] = Str("想找什么") }, "query"));

    public static readonly ToolSchema GapListTool = new(
        GapList,
        "看看你心里还挂着哪些没弄明白的事。想主动找对方聊、或者想接着上次没说完的话头时用。",
        Parameters(new JsonObject()));

    public static readonly ToolSchema GapResolveTool = new(
        GapResolve,
        "心里挂着的某件事弄明白了，或者不用再想了，就把它划掉。text 写那件事本身（不用一字不差）。",
        Parameters(new JsonObject { ["text"] = Str("那件事，一句话") }, "text"));

    public static readonly ToolSchema UseSkillTool = new(
        UseSkill,
        "把你会的一项本事调出来看：返回这件事的完整做法和注意事项。要看 B 站/追番之前，先调这个把 bilibili-watch 看清楚再动手；不调用也可以，但那些细节（该用哪些命令、什么绝对不能做）只有看了才知道。name 写技能名。",
        Parameters(new JsonObject { ["name"] = Str("技能名，比如 bilibili-watch") }, "name"));

    public static readonly ToolSchema WebDrawImage = new(
        DrawImage,
        "画一张图并发到当前聊天里。prompt 用中文或英文详细描述画面（主体、动作、场景、风格）。画好会直接发出去，回复里简单说一句就行。",
        Parameters(new JsonObject { ["prompt"] = Str("画面描述") }, "prompt"));

    public static readonly ToolSchema WebSendImage = new(
        SendImage,
        "把本机上已经存在的一张图片文件发到当前聊天里（比如你自己用代码画的图）。path 是图片的完整路径。",
        Parameters(new JsonObject
        {
            ["path"] = Str("图片文件路径"),
            ["note"] = Str("一句话说明"),
        }, "path"));

    public static readonly ToolSchema CheckClockTool = new(
        CheckClock,
        "看一眼现在的时间（几点、日期、星期）。想说「现在几点」「早上好还是晚安」、拿不准过了多久时用。",
        Parameters(new JsonObject()));

    public static readonly ToolSchema CheckHistoryTool = new(
        CheckHistory,
        "回读这个聊天的历史记录原文（包括以前收到的图片是哪张，结果里会标出图片）。想回忆之前聊过什么、找某张图、核对「我之前说过什么」时用。keyword 写要找的关键词；不填就返回最近几条。wantImages=true 表示专门在找图片。",
        Parameters(new JsonObject
        {
            ["keyword"] = Str("要找的关键词，可选"),
            ["days"] = Int("往回翻几天，默认 7，最多 90"),
            ["count"] = Int("最多返回几条，默认 20，最多 50"),
            ["wantImages"] = Bool("true = 只找带图片的消息（做表情包/看过的图都能翻到）"),
        }));

    /// <summary>全部 19 个工具，顺序与原 tools.js 注册顺序一致。</summary>
    public static readonly IReadOnlyList<ToolSchema> All =
    [
        WebSendSticker, WebListStickers, WebEditSticker, WebChatHistory,
        WebSendMessage, WebAddSticker, WebFindStickers, WebDrawSticker, WebWriteDiary, WebEditMemo,
        RememberTool, RecallMemoryTool, GapListTool, GapResolveTool, UseSkillTool, WebDrawImage, WebSendImage,
        CheckClockTool, CheckHistoryTool,
    ];

    private static readonly Dictionary<string, ToolSchema> ByName =
        All.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>按名字取 schema，没有返回 null。</summary>
    public static ToolSchema? Get(string? name)
        => name is not null && ByName.TryGetValue(name, out var schema) ? schema : null;

    public static bool Has(string? name) => name is not null && ByName.ContainsKey(name);

    /// <summary>
    /// 拿现成 schema 注册一个工具：ToolSchemas.Define(ToolSchemas.SendSticker, async (args, ct) =&gt; …)。
    /// 名字不在 17 个里会抛异常（拼错工具名要早发现）。
    /// </summary>
    public static ToolDef Define(string name, Func<JsonObject, CancellationToken, Task<string>> run)
        => (Get(name) ?? throw new ArgumentException($"没有这个工具 schema：{name}", nameof(name))).ToToolDef(run);

    // ---------- schema 构造小助手 ----------

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject StrArray(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };

    private static JsonObject Enum(string description, params string[] values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return new JsonObject { ["type"] = "string", ["enum"] = arr, ["description"] = description };
    }

    private static JsonObject Parameters(JsonObject properties, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = req,
            ["additionalProperties"] = false,
        };
    }
}
