using System.Text.Json.Nodes;

namespace AuraChat.Core.Features;

/// <summary>一个模型能调的工具（对齐原 tools/tools.js 的 17 个 schema）。</summary>
public sealed record ToolDef(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<string>> Run);

/// <summary>工具登记表：各功能模块自己把工具挂进来，聊天流水线只认这张表。</summary>
public interface IToolRegistry
{
    void Register(ToolDef tool);
    IReadOnlyList<ToolDef> All();
    Task<string> InvokeAsync(string name, JsonObject args, CancellationToken ct = default);

    /// <summary>按名字登记一个能跑的工具（聊天中枢给自装工具用；同名覆盖之前的声明）。</summary>
    void Register(string name, Func<JsonObject, CancellationToken, Task<string>> run);

    /// <summary>登记了多少个工具（schema + 可跑的总数）。</summary>
    int Count { get; }
}

/// <summary>记忆（features/memory.js）：召回、写入、压缩、管理面板。</summary>
public interface IMemoryService
{
    /// <summary>拼出给 system prompt 用的记忆上下文（原 recall 的产物，已格式化）。</summary>
    Task<string> RecallAsync(string subject, string userText, CancellationToken ct = default);

    /// <summary>一段对话结束后抽取/写入记忆。</summary>
    Task ObserveAsync(string subject, string userText, string botText, CancellationToken ct = default);

    JsonNode? Snapshot();

    // ---- 管理面（对齐原 web/routes/memory.js：GET api/memory 与 POST api/memory*）----
    JsonObject Stats();
    JsonArray List();
    JsonArray Insights();
    JsonObject Styles();
    JsonObject Profiles();
    JsonArray Recalls();
    string LastConsolidate();

    JsonObject Add(string text, double importance, IEnumerable<string> tags, string source);
    JsonObject Remove(string id);
    JsonObject Update(string id, string? text, double? importance, IEnumerable<string>? tags);
    JsonObject SetProfile(string about, string text, string name);
    JsonObject SetStyle(string who, JsonArray? lines);
    JsonObject SetInsights(string key, JsonArray? lines, string name);
    Task<JsonObject> ConsolidateAsync(bool includeToday, bool force, CancellationToken ct = default);
}

/// <summary>心情 / 激素 / 伴侣感（companion.js + hormones.js + gaps.js）。</summary>
public interface IMoodService
{
    /// <summary>温度劫持后的实际温度。</summary>
    Task<double> TemperatureAsync(double baseTemperature, CancellationToken ct = default);

    /// <summary>表情包自动配的概率倍数（未改动前 = 1.0）。</summary>
    double StickerFactor();

    /// <summary>气泡间隔倍数。</summary>
    double DelayFactor();

    /// <summary>拼进 system prompt 的心情行（原 contextLine，不自曝"你现在心情不错"）。</summary>
    string ContextLine();

    /// <summary>一轮对话后更新心情（原 judge/exchange）。</summary>
    Task AfterExchangeAsync(string userText, string botText, CancellationToken ct = default);

    /// <summary>闲置主动搭话的由头（原 speakup cue：缺口池张力 / 冲浪内容）。</summary>
    Task<string?> SpeakUpCueAsync(CancellationToken ct = default);

    JsonNode? Snapshot();

    // ---- 管理面（对齐原 web/routes/memory.js 的 api/mood 两条）----
    /// <summary>三轴状态（原 mood.state()）：dopamine/cortisol/serotonin 与派生量。</summary>
    JsonObject State();

    /// <summary>最近若干采样点（原 mood.history()）。</summary>
    JsonArray History();

    void Reset();

    /// <summary>手动调整只动「兴致」dopamine（原 mood.nudge）。</summary>
    void Nudge(double delta, string reason);
}

/// <summary>表情包库（features/stickers.js + stickersrc.js）：自动配、入库、搜索。</summary>
public interface IStickerService
{
    /// <summary>给这条回复挑一个表情包；返回 null = 不加。</summary>
    Task<string?> PickAsync(string botText, CancellationToken ct = default);

    /// <summary>真正的随机取一张（无正文匹配需求时用）。</summary>
    string? RandomSticker();

    Task<JsonObject> ListAsync();

    Task<JsonObject> AddAsync(string md5, byte[] bytes, string? label);

    /// <summary>表情文件在磁盘上的路径（md5 查库）；没有返回 null。</summary>
    string? PathOf(string md5);

    /// <summary>上传一张表情入库（对齐原 stickers.addFromBuffer）。</summary>
    Task<JsonObject> UploadAsync(byte[] data, string? name, IEnumerable<string>? tags, string? from, string? hint);

    Task<JsonObject> RemoveAsync(string md5);

    Task<JsonObject> RenameAsync(string md5, string? name, IEnumerable<string>? tags);

    JsonNode? Snapshot();
}

/// <summary>语音（voice/tts.js + wav.js）：合成、自动语音开关。</summary>
public interface IVoiceService
{
    /// <summary>把文字合成成音频临时文件（调用方负责读走并存进 MediaStore）。失败返回 reason。</summary>
    Task<(string? FilePath, string? Engine, string? Reason)> SynthesizeAsync(string text, CancellationToken ct = default);

    /// <summary>这条回复要不要自动转语音（对齐 voice.autoChance / autoMaxChars）。</summary>
    bool ShouldAutoSpeak(string text);

    /// <summary>edge-tts 组件当前状态 {installed, dir}。</summary>
    JsonObject EdgeState();

    /// <summary>装 edge-tts 语音组件（原 POST api/voice/edge）。</summary>
    Task<JsonObject> InstallEdgeAsync(CancellationToken ct = default);
}

/// <summary>空间 / 日记 / 留言（features/azone.js + azone-social.js）。</summary>
public interface IDiaryService
{
    /// <summary>按可见性过滤后的日记列表（owner 能看 self，客人只能看 all）。</summary>
    JsonArray VisibleEntries(bool owner);

    JsonObject State();

    JsonObject Stats();

    /// <summary>写一篇（原 azone.writeDiary）。visibility: all|self。</summary>
    Task<JsonObject> WriteAsync(string visibility = "all", CancellationToken ct = default);

    /// <summary>定时器用：到点写日记（原 POST api/test what=diary）。</summary>
    Task<JsonObject> RunDiaryAsync(string? reason = null, CancellationToken ct = default);

    /// <summary>定时器用：她自己去逛别人空间/回访留言（原 azoneSocial.run）。</summary>
    Task<JsonObject> RunSocialAsync(CancellationToken ct = default);

    JsonObject Remove(string entryId);

    JsonObject AddComment(string entryId, string from, string name, string text);

    JsonObject AddReply(string entryId, string commentId, string from, string name, string text);

    JsonObject DeleteComment(string entryId, string commentId);

    JsonObject DeleteReply(string entryId, string commentId, string replyId);

    /// <summary>查一条留言（权限判断要在路由层做）。</summary>
    JsonObject? CommentOf(string entryId, string commentId);

    /// <summary>查一条回复。</summary>
    JsonObject? ReplyOf(string entryId, string commentId, string replyId);

    JsonNode? Snapshot();
}

/// <summary>自驱（features/selfdrive.js）：定时自己找事做（冲浪/画表情/写日记）。</summary>
public interface ISelfDriveService : AuraChat.Core.Runtime.ISelfDriveMemo
{
    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();
}

/// <summary>聊天记录（features/history.js）：分片落盘、分页、过期清理。</summary>
public interface IHistoryStore
{
    /// <summary>追加一条消息（补齐 id/t），返回落盘后的完整对象。</summary>
    JsonObject Append(string chatId, JsonObject message);

    /// <summary>改最后一条（如语音 heard）。</summary>
    JsonObject PatchLast(string chatId, JsonObject patch);

    /// <summary>按 before 时间戳往前翻一页（limit ≤ 100）。</summary>
    (JsonArray Messages, long OldestAt, bool HasMore) Page(string chatId, long before, int limit);

    /// <summary>file = last|prev 的整片翻页（老客户端用）。</summary>
    (JsonArray Messages, long OldestAt, bool HasMore, int Index, int Total, string? FileId) PageFile(string chatId, string file, long before);

    JsonArray Recent(string chatId, int count);

    JsonObject? Last(string chatId);

    long OldestAt(string chatId);

    int TotalOf(string chatId);

    /// <summary>清过期片（返回丢了多少条）。</summary>
    JsonObject Sweep(string chatId);

    /// <summary>清所有会话的过期片，返回有变化的会话（要推 history 事件）。</summary>
    List<JsonObject> SweepAll();

    void Touch(string chatId);

    void Finalize(string chatId);

    /// <summary>
    /// 标成已读（D3/v3）：把水位推进到 <c>min(upToT, 现在)</c>，upToT 缺省＝现在，且只前推不回退。
    /// 返回（推进前的未读数, 推进后的水位）。
    /// </summary>
    Task<(int UnreadBefore, long ReadAt)> MarkReadAsync(string chatId, long? upToT = null, CancellationToken ct = default);

    /// <summary>对方最后的已读时间（毫秒；没读过 0）。</summary>
    long LastReadAt(string chatId);

    JsonObject Stats();
}

/// <summary>用户发来的一轮消息（原 onUserMessage 的入参）。</summary>
public sealed record UserMessage(
    string ChatId,
    string UserId,
    string Nickname,
    string Text,
    JsonArray Images,
    VoiceInput? Voice,
    string Sticker,
    string Level = "");

public sealed record VoiceInput(string Data, string Mime, double Dur);

/// <summary>聊天编排（chat/hub.js）：合并等待、忙碌锁、调模型、拆气泡发送。</summary>
public interface IChatHub
{
    Task<bool> OnUserMessageAsync(UserMessage message, CancellationToken ct = default);

    /// <summary>对方正在打字（原 api/typing 的 typingUntil：这段时间先别急着回）。</summary>
    void NotifyTyping(string chatId, bool typing);

    /// <summary>闲置主动搭话（原 speakup）。</summary>
    Task<bool> SpeakUpAsync(CancellationToken ct = default);

    /// <summary>自驱推送（selfdrive → 聊天层）：cue 是她刚看到的/做了的事，让她用自己的口吻发给主人。</summary>
    Task<bool> SelfDrivePushAsync(string cue, CancellationToken ct = default);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    JsonObject State();
}
