using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Ai;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;

namespace AuraChat.Features.Chat;

/// <summary>
/// 聊天编排（对齐原 chat/hub.js）：合并连发、忙碌锁、拼 prompt、调模型、拆气泡发送。
/// 一个进程里只有一个实例，所有会话共用。
/// </summary>
public sealed class ChatHub : IChatHub
{
    private const int MergeMaxMs = 8000;
    private const int TypingHoldMaxMs = 25_000;
    private const int BusyMaxMs = 240_000;
    /// <summary>上下文里最近多少条原文不压（保留新鲜感）。</summary>
    public const int ContextMessageCount = 30;

    private sealed class Pending
    {
        public required string ChatId { get; init; }
        public required string UserId { get; init; }
        public required string Nickname { get; init; }
        public List<string> Texts { get; } = [];
        public List<JsonObject> Images { get; } = [];
        public bool WantVoice { get; set; }
        public DateTime FirstAt { get; } = DateTime.UtcNow;
        public CancellationTokenSource? Timer { get; set; }
        public int Count { get; set; }
        public string LastUserText { get; set; } = "";
    }

    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly HashSet<string> _busy = [];
    private readonly Dictionary<string, DateTime> _typingUntil = [];
    private readonly Dictionary<string, DateTime> _lastBot = [];
    private readonly Dictionary<string, (DateTime At, string Text)> _lastBotToOwner = [];
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _ticker;

    public ChatHub(FeatureHost host)
    {
        _host = host;
        _log = host.Log.Child("chat");
        host.Set<IChatHub>(this);
        RegisterTests();
    }

    private AppLog Log => _host.Log;
    private IHistoryStore? History => _host.Get<IHistoryStore>();
    private ILlmClient? Llm => _host.Get<ILlmClient>();
    private IMemoryService? Memory => _host.Get<IMemoryService>();
    private IMoodService? Mood => _host.Get<IMoodService>();
    private IStickerService? Stickers => _host.Get<IStickerService>();
    private IVoiceService? Voice => _host.Get<IVoiceService>();

    // ---------------------------------------------------------------- 生命周期

    public Task StartAsync(CancellationToken ct = default)
    {
        RegisterSelfTools();
        if (_ticker is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ticker = Task.Run(() => TickLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>把 check_clock / check_history 两个回读工具挂进注册表（她按需查时间/翻聊天记录）。</summary>
    private void RegisterSelfTools()
    {
        if (_host.Get<IToolRegistry>() is not { } tools) return;

        tools.Register(ToolSchemas.CheckClock, (_, _) =>
        {
            var now = DateTime.Now;
            var cn = now.ToString("yyyy-MM-dd HH:mm:ss") + " " + "日一二三四五六"[(int)now.DayOfWeek] + "曜日";
            var hour = now.Hour;
            var period = hour switch
            {
                >= 5 and < 8 => "清晨",
                < 11 => "上午",
                < 13 => "中午",
                < 18 => "下午",
                < 23 => "晚上",
                _ => "深夜",
            };
            return Task.FromResult($"现在是 {cn}（{period}，{hour} 点）。");
        });

        tools.Register(ToolSchemas.CheckHistory, (args, ct) => CheckHistoryAsync(args, ct));
    }

    /// <summary>回读聊天记录：按关键词/天数/条数查原文，wantImages=true 只翻带图的（表情包场景）。</summary>
    private async Task<string> CheckHistoryAsync(JsonObject args, CancellationToken ct)
    {
        var history = History;
        if (history is null) return "聊天记录还没装上。";

        var keyword = args["keyword"]?.ToString() ?? "";
        var days = Math.Clamp((int)JsonUtil.Num(args["days"], 7), 1, 90);
        var count = Math.Clamp((int)JsonUtil.Num(args["count"], 20), 1, 50);
        var wantImages = JsonUtil.Bool(args["wantImages"], false);

        var since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
        var hits = new List<JsonObject>();
        long cursor = 0;
        var scanned = 0;
        const int scanCap = 2000;

        while (hits.Count < count && scanned < scanCap)
        {
            var (page, oldestAt, hasMore) = history.Page(AuraChatInfo.OwnerId, cursor, 100);
            if (page.Count == 0) break;
            scanned += page.Count;
            foreach (var m in page)
            {
                if (m is not JsonObject msg) continue;
                var t = msg["t"]?.GetValue<long>() ?? 0;
                if (t < since) continue;
                if (wantImages && msg["mediaId"] is null) continue;
                var text = msg["text"]?.ToString() ?? "";
                if (keyword.Length > 0 && !text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    // 图片消息按文件名/备注再试一次
                    if (!(wantImages && msg["mediaMime"]?.ToString()?.StartsWith("image") == true && keyword.Length == 0))
                        continue;
                }
                hits.Add((JsonObject)msg.DeepClone());
            }
            if (!hasMore || oldestAt <= 0) break;
            cursor = oldestAt;
        }

        if (hits.Count == 0)
            return wantImages ? $"最近 {days} 天没翻到带图的消息。" : $"最近 {days} 天没翻到含「{keyword}」的消息。";

        var lines = new List<string> { $"【回读 · 最近{days}天内 {hits.Count} 条】" };
        foreach (var m in hits)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(m["t"]?.GetValue<long>() ?? 0)
                .ToLocalTime().ToString("MM-dd HH:mm");
            var who = m["who"]?.ToString() == "bot" ? "你" : "对方";
            var kind = m["kind"]?.ToString() ?? "text";
            var body = kind switch
            {
                "image" => $"[图片：mediaId={m["mediaId"]}]",
                "voice" => $"[语音 {m["dur"]}秒]",
                "sticker" => $"[表情：{m["name"]}]",
                _ => m["text"]?.ToString() ?? "",
            };
            lines.Add($"{time} {who}: {body}");
        }
        if (wantImages)
            lines.Add("（要引用哪张图做表情，把那次的 mediaId 记下来配合 web_add_sticker 用。）");
        return string.Join("\n", lines);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }
        if (_ticker is not null)
        {
            try { await _ticker; } catch { /* 收尾 */ }
            _ticker = null;
        }
    }

    /// <summary>每 30 秒看一眼：闲置搭话、过期的 typing 锁。</summary>
    private async Task TickLoopAsync(CancellationToken ct)
    {
        var lastSweep = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await SpeakUpIfIdleAsync(ct);
                if (DateTime.UtcNow - lastSweep > TimeSpan.FromHours(1))
                {
                    lastSweep = DateTime.UtcNow;
                    SweepHistory();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.Warn("定时检查出错：" + ex.Message); }
        }
    }

    private void SweepHistory()
    {
        var history = History;
        if (history is null) return;
        try
        {
            foreach (var row in history.SweepAll())
            {
                _host.Emit("history", new JsonObject
                {
                    ["chatId"] = row["chatId"]?.ToString() ?? "",
                    ["oldestAt"] = JsonUtil.Num(row["oldestAt"], 0),
                    ["droppedBefore"] = JsonUtil.Num(row["oldestAt"], 0),
                    ["hasMore"] = false,
                });
            }
        }
        catch (Exception ex) { _log.Warn("清理过期聊天出错：" + ex.Message); }
    }

    // ---------------------------------------------------------------- 收到消息

    public void NotifyTyping(string chatId, bool typing)
    {
        lock (_gate)
        {
            if (typing) _typingUntil[chatId] = DateTime.UtcNow.AddSeconds(5);
            else _typingUntil.Remove(chatId);
        }
        if (typing) return;
        // 不再打字了：把等待中的消息立刻放出去
        Schedule(chatId, force: true);
    }

    public async Task<bool> OnUserMessageAsync(UserMessage message, CancellationToken ct = default)
    {
        var cfg = _host.Config.Current;
        if (!JsonUtil.Bool(cfg["enabled"], true)) return false;

        var chatId = message.ChatId;
        lock (_gate) { _typingUntil.Remove(chatId); }

        var content = new List<string>();
        var images = new List<JsonObject>();

        if (message.Voice is not null && !string.IsNullOrEmpty(message.Voice.Data))
        {
            // 独立版暂时没有语音识别：如实告诉她"只听到声音"，别猜。
            content.Add($"【对方发来一条 {(int)Math.Round(message.Voice.Dur)} 秒的语音，这段没转成文字，只听到声音】");
        }
        else if (!string.IsNullOrEmpty(message.Sticker))
        {
            var name = await StickerNameOfAsync(message.Sticker);
            content.Add($"【对方发来一张表情包{(name.Length > 0 ? "：" + name : "")}，看着接梗，或者也回一个 <sticker:标签>】");
        }
        else if (!string.IsNullOrWhiteSpace(message.Text))
        {
            content.Add(message.Text);
        }

        foreach (var img in message.Images)
        {
            if (img is JsonObject io) images.Add((JsonObject)io.DeepClone());
        }
        if (images.Count > 0)
            content.Add($"【对方发来 {images.Count} 张图片】");

        if (content.Count == 0) return false;

        Enqueue(chatId, message.UserId, message.Nickname, content, images, message.Voice is not null);
        return true;
    }

    private async Task<string> StickerNameOfAsync(string md5)
    {
        var stickers = Stickers;
        if (stickers is null) return "";
        try
        {
            var list = await stickers.ListAsync();
            foreach (var item in list["items"] as JsonArray ?? new JsonArray())
            {
                if (item?["md5"]?.ToString() == md5) return item["name"]?.ToString() ?? "";
            }
        }
        catch { /* 名字拿不到不影响 */ }
        return "";
    }

    private void Enqueue(string chatId, string userId, string nickname, List<string> texts, List<JsonObject> images, bool wantVoice)
    {
        Pending p;
        lock (_gate)
        {
            if (!_pending.TryGetValue(chatId, out p!))
            {
                p = new Pending { ChatId = chatId, UserId = userId, Nickname = nickname };
                _pending[chatId] = p;
            }
            p.Count++;
            p.Texts.AddRange(texts);
            p.Images.AddRange(images);
            p.WantVoice |= wantVoice;
            p.LastUserText = string.Join("\n", p.Texts);
        }
        Schedule(chatId);
    }

    private void Schedule(string chatId, bool force = false)
    {
        Pending? p;
        lock (_gate)
        {
            if (!_pending.TryGetValue(chatId, out p)) return;
            p.Timer?.Cancel();
            p.Timer?.Dispose();
            p.Timer = null;
        }

        var cfg = _host.Config.Current;
        var mergeEnabled = JsonUtil.Bool(cfg["merge"]?["enabled"], true);
        var waitSec = mergeEnabled ? Math.Max(0, JsonUtil.Num(cfg["merge"]?["waitSec"], 2.5)) : 0;
        var maxSec = JsonUtil.Num(cfg["merge"]?["maxSec"], 8);
        var delayMs = (int)(Math.Min(waitSec, maxSec) * 1000);

        // 对方正在打字就先等等（但不能无限等）
        if (!force)
        {
            lock (_gate)
            {
                if (_typingUntil.TryGetValue(chatId, out var until))
                {
                    var left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                    var elapsed = (DateTime.UtcNow - p.FirstAt).TotalMilliseconds;
                    if (left > 0 && elapsed < TypingHoldMaxMs) delayMs = Math.Max(delayMs, left);
                }
            }
        }

        var cts = new CancellationTokenSource();
        lock (_gate) { if (_pending.TryGetValue(chatId, out var q)) q.Timer = cts; }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await FlushAsync(chatId);
            }
            catch (OperationCanceledException) { /* 又来新消息了，重排 */ }
            catch (Exception ex) { _log.Warn("合并等待出错：" + ex.Message); }
        }, CancellationToken.None);
    }

    // ---------------------------------------------------------------- 主流程

    private async Task FlushAsync(string chatId)
    {
        Pending? p;
        lock (_gate)
        {
            if (!_pending.TryGetValue(chatId, out p)) return;
            _pending.Remove(chatId);
        }

        lock (_gate)
        {
            if (_busy.Contains(chatId))
            {
                // 上一轮还在说，等它说完再来（把内容放回去，避免丢话）
                _pending[chatId] = p;
                return;
            }
            _busy.Add(chatId);
        }

        try
        {
            await RespondAsync(p);
        }
        catch (Exception ex)
        {
            _log.Warn("这一轮没走完：" + ex.Message);
            _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = false });
            try { Mood?.Nudge(double.NaN, "刚才那轮出错了"); } catch { /* 忽略 */ }
        }
        finally
        {
            lock (_gate) { _busy.Remove(chatId); }
            // 说话期间又来了新消息 → 接着处理
            bool again;
            lock (_gate) { again = _pending.ContainsKey(chatId); }
            if (again) Schedule(chatId, force: true);
        }
    }

    private async Task RespondAsync(Pending p)
    {
        var llm = Llm;
        if (llm is null)
        {
            _log.Warn("模型还没配好，这轮不回了");
            return;
        }

        var cfg = _host.Config.Current;
        var chatId = p.ChatId;
        _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = true });

        var system = await BuildSystemPromptAsync(p);
        var messages = BuildMessages(p, system);
        var temperature = JsonUtil.Num(cfg["model"]?["temperature"], 1.0);
        if (Mood is not null)
        {
            try { temperature = await Mood.TemperatureAsync(temperature); }
            catch { /* 用原温度 */ }
        }
        var options = BuildOptions(cfg, system, temperature);

        string reply;
        try
        {
            // 有登记过的工具就走工具循环（模型可能调 check_clock / web_add_sticker 等），否则纯文本
            var tools = _host.Get<IToolRegistry>();
            if (tools is { Count: > 0 })
            {
                var loop = new ToolLoop(llm, tools, Log.Child("tools"));
                reply = await loop.RunAsync(options, messages, default);
            }
            else
            {
                reply = await llm.AskAsync(options, messages);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("模型没回话：" + ex.Message);
            _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = false });
            return;
        }

        await DeliverAsync(chatId, reply, p.WantVoice, p.LastUserText);

        var spoken = StripTags(reply);
        if (Memory is not null && spoken.Length > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await Memory.ObserveAsync(chatId, p.LastUserText, spoken); }
                catch (Exception ex) { _log.Warn("记记忆出错：" + ex.Message); }
            });
        }
        if (Mood is not null && spoken.Length > 0)
        {
            _ = Task.Run(async () =>
            {
                try { await Mood.AfterExchangeAsync(p.LastUserText, spoken); }
                catch (Exception ex) { _log.Warn("更新心情出错：" + ex.Message); }
            });
        }
    }

    private async Task<string> BuildSystemPromptAsync(Pending p)
    {
        var cfg = _host.Config.Current;
        var personaId = cfg["persona"]?["current"]?.ToString();
        var parts = new List<string>
        {
            _host.Persona.CurrentText(personaId),
        };

        var kind = p.UserId == AuraChatInfo.OwnerId ? "private" : "social";
        parts.Add(PersonaService.RulesText(kind, _host.Config));

        if (Mood is not null)
        {
            try
            {
                var line = Mood.ContextLine();
                if (!string.IsNullOrWhiteSpace(line)) parts.Add(line);
            }
            catch { /* 心情行拿不到不影响 */ }
        }

        foreach (var provider in _host.ContextProviders)
        {
            try
            {
                foreach (var line in provider.ContextLines())
                    if (!string.IsNullOrWhiteSpace(line)) parts.Add(line);
            }
            catch (Exception ex) { _log.Warn("上下文出错：" + ex.Message); }
        }

        if (Memory is not null)
        {
            try
            {
                var recall = await Memory.RecallAsync(p.ChatId, p.LastUserText);
                if (!string.IsNullOrWhiteSpace(recall)) parts.Add(recall);
            }
            catch (Exception ex) { _log.Warn("回忆出错：" + ex.Message); }
        }

        // 上下文压缩摘要（C2）：被压出窗口的旧对话用摘要顶上，原文永远在聊天记录里
        try
        {
            var summary = _host.Get<ContextCompressor>()?.SummaryOf(p.ChatId);
            if (summary?["summary"] is { } sum && !string.IsNullOrWhiteSpace(sum.ToString()))
            {
                var to = summary["coversTo"]?.ToString();
                var when = long.TryParse(to, out var toMs)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(toMs).ToLocalTime().ToString("MM-dd HH:mm")
                    : "早些时候";
                parts.Add($"【更早的对话（截至 {when}）的摘要】\n{sum}");
            }
        }
        catch { /* 摘要拿不到不影响 */ }

        return string.Join("\n\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private List<LlmMessage> BuildMessages(Pending p, string system)
    {
        var messages = new List<LlmMessage>();

        // 最近几轮对话当上下文（原版由 DSH 会话保存，这里从聊天记录里取）
        var history = History;
        if (history is not null)
        {
            try
            {
                var recent = history.Recent(p.ChatId, ContextMessageCount);
                foreach (var node in recent)
                {
                    if (node is not JsonObject m) continue;
                    var who = m["who"]?.ToString();
                    var kind = m["kind"]?.ToString();
                    if (kind is not ("text" or null)) continue;
                    var text = m["text"]?.ToString() ?? "";
                    if (text.Length == 0) continue;
                    messages.Add(new LlmMessage(who == "bot" ? "assistant" : "user", text));
                }
            }
            catch (Exception ex) { _log.Warn("取上下文出错：" + ex.Message); }
        }

        // 本轮内容
        var sb = new StringBuilder();
        sb.Append($"【{(p.UserId == AuraChatInfo.OwnerId ? "主人" : "客人")}】{p.Nickname}：");
        sb.Append(string.Join("\n", p.Texts.Where(t => t.Length > 0)));
        if (p.Images.Count > 0) sb.Append($"\n（还发了 {p.Images.Count} 张图片）");

        // 图片走多模态：本条 user 消息的 content 是 [文本, image_url, ...]（模型不支持时会报错回落纯文本，见 Catch）
        var imageParts = BuildImagePartsForTest(_host, p.ChatId, p.Images);
        if (imageParts.Count > 0)
        {
            var contentArr = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = sb.ToString() },
            };
            foreach (var part in imageParts) contentArr.Add(part.DeepClone());
            messages.Add(new LlmMessage("user", contentArr.ToJsonString()));
        }
        else
        {
            messages.Add(new LlmMessage("user", sb.ToString()));
        }

        // system 走 options.System，不塞 messages
        _ = system;
        return messages;
    }

    /// <summary>把图片转成多模态 content parts（data:image;base64）。文件缺失/读不了的跳过。public 供测试直调。</summary>
    public static List<JsonObject> BuildImagePartsForTest(FeatureHost host, string chatId, List<JsonObject> images)
    {
        var parts = new List<JsonObject>();
        var media = host.Get<MediaStore>();
        if (media is null) return parts;
        foreach (var img in images)
        {
            var mediaId = img["mediaId"]?.ToString() ?? "";
            var mime = img["mediaMime"]?.ToString() ?? "image/png";
            if (mediaId.Length == 0) continue;
            try
            {
                var path = media.PathOf(chatId, mediaId);
                if (path is null) continue;
                var bytes = File.ReadAllBytes(path);
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}",
                    },
                });
            }
            catch { /* 单张图读不了不影响其余 */ }
        }
        return parts;
    }

    private LlmOptions BuildOptions(JsonObject cfg, string? system = null, double? temperature = null)
    {
        var model = cfg["model"];
        return new LlmOptions
        {
            Provider = model?["provider"]?.ToString() ?? "",
            Model = model?["model"]?.ToString() ?? "",
            System = system ?? "",
            Temperature = Math.Clamp(temperature ?? JsonUtil.Num(model?["temperature"], 1.0), 0, 2),
            MaxTokens = Math.Max(64, JsonUtil.Int(model?["maxTokens"], 1024)),
            ReasoningEffort = model?["reasoningEffort"]?.ToString() ?? "low",
            TimeoutMs = Math.Max(5000, JsonUtil.Int(model?["timeoutMs"], 120_000)),
        };
    }

    // ---------------------------------------------------------------- 发送

    private async Task DeliverAsync(string chatId, string reply, bool wantVoice, string userText)
    {
        var cfg = _host.Config.Current;
        var parsed = ReplyParser.ParseDetailed(reply);
        if (parsed.Silent)
        {
            _log.Info($"{chatId}：她选择不说话");
            _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = false });
            return;
        }

        var items = parsed.Items.Where(i => i.IsSendable).ToList();

        // 发送前自查：把"她写给自己看的旁白"挑出来删掉
        if (items.Any(i => i.Kind == ReplyKind.Text))
        {
            var guard = new Guard(_host);
            items = await guard.FilterAsync(items);
            if (items.Count == 0)
            {
                _log.Info($"{chatId}：自查后没剩下能发的话");
                _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = false });
                return;
            }
        }

        var voiceEnabled = JsonUtil.Bool(cfg["voice"]?["enabled"], false);

        // 对方要语音 → 整段改成语音
        if (wantVoice && voiceEnabled && items.All(i => i.Kind != ReplyKind.Voice))
        {
            var spoken = string.Join("，", items.Where(i => i.Kind == ReplyKind.Text).Select(i => i.Text));
            if (spoken.Length is > 0 and <= 300)
                items = [new ReplyItem(ReplyKind.Voice, spoken), .. items.Where(i => i.Kind != ReplyKind.Text)];
        }

        // 全是文字且够短 → 按 autoChance 整段转语音
        var textOnly = items.Where(i => i.Kind == ReplyKind.Text).ToList();
        if (voiceEnabled && textOnly.Count == items.Count && textOnly.Count > 0)
        {
            var all = string.Join("，", textOnly.Select(i => i.Text));
            var maxChars = Math.Max(1, JsonUtil.Int(cfg["voice"]?["autoMaxChars"], 80));
            var chance = Math.Clamp(JsonUtil.Num(cfg["voice"]?["autoChance"], 0.15), 0, 1);
            if (all.Length <= maxChars && Random.Shared.NextDouble() < chance)
                items = [new ReplyItem(ReplyKind.Voice, all)];
        }

        // 自动配表情包
        var stickersEnabled = JsonUtil.Bool(cfg["stickers"]?["enabled"], true);
        if (stickersEnabled && items.All(i => i.Kind != ReplyKind.Sticker) && Stickers is not null)
        {
            var chance = Math.Clamp(JsonUtil.Num(cfg["stickers"]?["autoChance"], 0.35), 0, 1);
            var factor = Mood?.StickerFactor() ?? 1.0;
            if (Random.Shared.NextDouble() < chance * factor)
            {
                var pick = await Stickers.PickAsync(string.Join(" ", textOnly.Select(i => i.Text)));
                if (pick is not null) items.Add(ReplyItem.OfSticker(pick));
            }
        }

        var delayFactor = Mood?.DelayFactor() ?? 1.0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            switch (item.Kind)
            {
                case ReplyKind.Text:
                    SendText(chatId, item.Text);
                    break;
                case ReplyKind.Voice:
                    if (voiceEnabled) await SendVoiceAsync(chatId, item.Text);
                    else SendText(chatId, item.Text);
                    break;
                case ReplyKind.Sticker:
                    SendSticker(chatId, item.StickerTag);
                    break;
                case ReplyKind.Image:
                    _log.Debug("图片消息暂时跳过：" + item.Text);
                    break;
            }

            if (i < items.Count - 1)
            {
                var next = items[i + 1];
                var delay = next.Kind == ReplyKind.Text
                    ? ReplyParser.BubbleDelayMs(next.Text, delayFactor)
                    : ReplyParser.BubbleDelayMs(6, delayFactor);
                await Task.Delay(delay);
            }
        }

        _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = false });
        LogChat(chatId, "bot", string.Join(" ", items.Select(i => i.Kind is ReplyKind.Text or ReplyKind.Voice ? i.Text : "[" + i.Kind + "]")));

        lock (_gate) { _lastBot[chatId] = DateTime.UtcNow; }
        if (chatId == AuraChatInfo.OwnerId)
        {
            var spoken = string.Join("\n", items.Where(i => i.Kind is ReplyKind.Text or ReplyKind.Voice).Select(i => i.Text));
            lock (_gate) { _lastBotToOwner[chatId] = (DateTime.UtcNow, spoken); }
        }
        _ = userText;
    }

    private void SendText(string chatId, string text)
    {
        var history = History;
        if (history is null || string.IsNullOrWhiteSpace(text)) return;
        var saved = history.Append(chatId, new JsonObject
        {
            ["who"] = "bot",
            ["text"] = text,
            ["kind"] = "text",
        });
        history.Touch(chatId);
        _host.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = saved.DeepClone() });
        LogChat(chatId, "bot", text);
    }

    private async Task SendVoiceAsync(string chatId, string text)
    {
        var voice = Voice;
        if (voice is null) { SendText(chatId, text); return; }

        string? file = null;
        try
        {
            var (path, engine, reason) = await voice.SynthesizeAsync(text);
            if (path is null || !File.Exists(path))
            {
                _log.Warn("语音没合成出来，改发文字：" + (reason ?? "不知道为啥"));
                SendText(chatId, text);
                return;
            }

            file = path;
            var bytes = await File.ReadAllBytesAsync(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                _ => "audio/ogg",
            };
            var media = _host.Get<MediaStore>();
            var history = History;
            if (media is null || history is null) { SendText(chatId, text); return; }

            var (mediaId, size) = media.Save(chatId, bytes, mime);
            var saved = history.Append(chatId, new JsonObject
            {
                ["who"] = "bot",
                ["text"] = text,
                ["kind"] = "voice",
                ["mediaId"] = mediaId,
                ["mediaMime"] = mime,
                ["bytes"] = size,
                ["engine"] = engine ?? "",
            });
            history.Touch(chatId);
            _host.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = saved.DeepClone() });
            LogChat(chatId, "bot", text);
        }
        catch (Exception ex)
        {
            _log.Warn("语音没发出去，改发文字：" + ex.Message);
            SendText(chatId, text);
        }
        finally
        {
            try { if (file is not null && File.Exists(file)) File.Delete(file); } catch { /* 临时文件删不掉就算了 */ }
        }
    }

    internal void SendSticker(string chatId, string tag)
    {
        var stickers = Stickers;
        var history = History;
        if (stickers is null || history is null) return;

        string? md5 = null;
        try
        {
            var pick = stickers.PickAsync(tag).GetAwaiter().GetResult();
            md5 = pick ?? stickers.RandomSticker();
        }
        catch (Exception ex) { _log.Warn("挑表情出错：" + ex.Message); }

        if (md5 is null) { _log.Debug("表情库是空的，跳过表情"); return; }

        var saved = history.Append(chatId, new JsonObject
        {
            ["who"] = "bot",
            ["text"] = "[表情]",
            ["kind"] = "sticker",
            ["sticker"] = md5,
            ["name"] = tag,
            ["caption"] = tag,
            ["mark"] = "sticker",
        });
        history.Touch(chatId);
        _host.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = saved.DeepClone() });
        LogChat(chatId, "bot", "[表情]");
    }

    // ---------------------------------------------------------------- 主动搭话

    private DateTime _lastSpeakUp = DateTime.MinValue;
    private int _speakUpToday;
    private string _speakUpDay = "";

    public async Task<bool> SpeakUpIfIdleAsync(CancellationToken ct = default)
    {
        var cfg = _host.Config.Current;
        var speakUp = cfg["speakUp"];
        if (speakUp is null || !JsonUtil.Bool(speakUp["enabled"], true)) return false;
        if (Llm is null || History is null) return false;

        var chatId = AuraChatInfo.OwnerId;
        var history = History;
        if (history.TotalOf(chatId) == 0) return false;

        var idleMinutes = Math.Max(5, JsonUtil.Int(speakUp["idleMinutes"], 60));
        var minGapMinutes = Math.Max(5, JsonUtil.Int(speakUp["minGapMinutes"], 120));
        var now = DateTime.UtcNow;

        DateTime last;
        lock (_gate) { last = _lastBot.TryGetValue(chatId, out var t) ? t : now; }
        if (now - last < TimeSpan.FromMinutes(idleMinutes)) return false;
        if (now - _lastSpeakUp < TimeSpan.FromMinutes(minGapMinutes)) return false;
        if (now.Hour < 8 || now.Hour >= 23) return false;

        var today = now.ToString("yyyy-MM-dd");
        if (_speakUpDay != today) { _speakUpDay = today; _speakUpToday = 0; }
        if (_speakUpToday >= Math.Max(0, JsonUtil.Int(speakUp["maxPerDay"], 4))) return false;

        string cue = "";
        if (Mood is not null)
        {
            try { cue = await Mood.SpeakUpCueAsync(ct) ?? ""; }
            catch (Exception ex) { _log.Warn("取搭话由头出错：" + ex.Message); }
        }

        return await SpeakUpCoreAsync(chatId, cue, ct);
    }

    public async Task<bool> SpeakUpAsync(CancellationToken ct = default)
    {
        return await SpeakUpCoreAsync(AuraChatInfo.OwnerId, "", ct);
    }

    /// <summary>自驱（selfdrive）推送入口：把"她刚刷到/自己做了什么"喂给她，让她用自己的口气发给主人。</summary>
    public async Task<bool> SelfDrivePushAsync(string cue, CancellationToken ct = default)
    {
        return await SpeakUpCoreAsync(AuraChatInfo.OwnerId, cue, ct);
    }

    private async Task<bool> SpeakUpCoreAsync(string chatId, string cue, CancellationToken ct)
    {
        var llm = Llm;
        if (llm is null) return false;

        var cfg = _host.Config.Current;
        var p = new Pending { ChatId = chatId, UserId = AuraChatInfo.OwnerId, Nickname = cfg["ownerName"]?.ToString() ?? "主人" };
        p.LastUserText = "【系统提醒：已经很久没人说话了，你想主动找他说点什么】";

        var system = await BuildSystemPromptAsync(p);
        var options = BuildOptions(cfg, system);
        var messages = new List<LlmMessage>
        {
            new("user", "【系统提醒：你一个人待了一会儿了。想找他说话就写一两句；" +
                        (cue.Length > 0 ? $"刚才你心里惦记着这个：{cue}。" : "") +
                        "不想说话就只输出 [沉默]。】"),
        };

        string reply;
        try { reply = await llm.AskAsync(options, messages, ct); }
        catch (Exception ex) { _log.Warn("主动搭话失败：" + ex.Message); return false; }
        if (string.IsNullOrWhiteSpace(reply)) return false;

        await DeliverAsync(chatId, reply, false, "【系统提醒：主动搭话】");
        _lastSpeakUp = DateTime.UtcNow;
        _speakUpToday++;
        return true;
    }

    // ---------------------------------------------------------------- 杂项

    private void LogChat(string chatId, string who, string text, string uid = "")
    {
        try
        {
            var dir = Path.Combine(_host.Store.DataDir, "chatlog");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
            var row = new JsonObject
            {
                ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["chatId"] = chatId,
                ["who"] = who,
                ["text"] = text,
            };
            if (uid.Length > 0) row["uid"] = uid;
            File.AppendAllText(file, row.ToJsonString() + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { _log.Warn("写聊天日志出错：" + ex.Message); }
    }

    private static string StripTags(string reply)
    {
        var text = reply;
        foreach (var tag in new[] { "<sticker:", "<voice>", "</voice>", "<img>" })
        {
            _ = tag;
        }
        // 去掉各种尖括号标记，只留正文
        return System.Text.RegularExpressions.Regex.Replace(text, "<[^>]{0,80}>", "").Trim();
    }

    public JsonObject State()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["pending"] = _pending.Count,
                ["busy"] = _busy.Count,
                ["lastBot"] = _lastBot.Count > 0 ? _lastBot.Values.Max().ToString("O") : "",
            };
        }
    }

    // ---------------------------------------------------------------- 自检（api/test）

    /// <summary>1×1 PNG，给自检当测试图/测试表情用。</summary>
    internal static readonly byte[] TinyPng =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private void RegisterTests()
    {
        // 回归问题1「乱发表情包」：回复里没写 <sticker:标签> 时，软件不许自己配表情。
        _host.AddTest(new TestAction("聊天：回复不自动配表情", async () =>
        {
            var stickers = Stickers;
            if (stickers is null) return new JsonObject { ["ok"] = false, ["reason"] = "表情库没装上" };
            // 确保库里有一张（不然自动配表情那步会因为挑不到而侥幸通过）
            var list = await stickers.ListAsync();
            if ((list["items"] as JsonArray)?.Count == 0)
                await stickers.UploadAsync(TinyPng, "回归测试表情", new[] { "测试" }, "test", null);

            var before = CountRecentKind(AuraChatInfo.OwnerId, "sticker");
            await DeliverAsync(AuraChatInfo.OwnerId, "好呀 今天也要一起加油", wantVoice: false, userText: "自检");
            var after = CountRecentKind(AuraChatInfo.OwnerId, "sticker");
            var added = after - before;
            return new JsonObject
            {
                ["ok"] = added <= 0,
                ["added"] = added,
                ["note"] = added > 0 ? "软件替她自动配了表情（不该发生）" : "纯文字回复没有自动配表情",
            };
        }));

        // 回归问题4「不能一次发多张照片」：回复里的 ![alt](图源) 标记要真能发成图片消息。
        _host.AddTest(new TestAction("聊天：图片标记能发出去", async () =>
        {
            var media = _host.Get<MediaStore>();
            if (media is null) return new JsonObject { ["ok"] = false, ["reason"] = "媒体库没装上" };
            var (id, _) = media.Save(AuraChatInfo.OwnerId, TinyPng, "image/png");

            var before = CountRecentKind(AuraChatInfo.OwnerId, "image");
            await DeliverAsync(AuraChatInfo.OwnerId, $"给你看看 ![小图](media:{id})", wantVoice: false, userText: "自检");
            var after = CountRecentKind(AuraChatInfo.OwnerId, "image");
            var added = after - before;
            return new JsonObject
            {
                ["ok"] = added == 1,
                ["added"] = added,
                ["note"] = added == 0 ? "图片标记被跳过了，没发出去" : "图片消息发出去了",
            };
        }));
    }

    private int CountRecentKind(string chatId, string kind)
    {
        try
        {
            return History?.Recent(chatId, 30).Count(n => n["kind"]?.ToString() == kind) ?? -1;
        }
        catch { return -1; }
    }

    private sealed class TestAction(string name, Func<Task<JsonObject>> run) : ITestAction
    {
        public string Name => name;
        public Task<JsonObject> RunAsync() => run();
    }
}
