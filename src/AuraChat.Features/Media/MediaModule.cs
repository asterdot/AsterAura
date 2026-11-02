using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Media;
using AuraChat.Core.Runtime;
using AuraChat.Features.Chat;

namespace AuraChat.Features.Media;

/// <summary>
/// Media 功能装配（对应原 features/index.js 里表情包 / 画画 / 日记 / 自驱 / 冲浪 / 语音部分）：
/// 建服务、挂接口、接工具执行函数、摆状态面板和测试按钮。
/// 每分钟 tick（日记到点写、空间到点逛）由 AppHost 的 ticker 调 MediaModule.TickAsync。
/// </summary>
public static class MediaModule
{
    public static MediaStore Create(FeatureHost host)
    {
        var media = new MediaStore(host.Store, host.Log);
        var stickers = new StickerService(host);
        var images = new ImageGenService(host, media);
        var surf = new SurfService(host);
        var voice = new VoiceService(host);
        var diary = new DiaryService(host);
        var selfDrive = new SelfDriveService(host, stickers, images, surf);

        host.Set(media);
        host.Set(stickers);
        host.Set<IStickerService>(stickers);
        host.Set(images);
        host.Set(surf);
        host.Set<ISurfModule>(surf);
        host.Set(voice);
        host.Set<IVoiceService>(voice);
        host.Set(diary);
        host.Set<IDiaryService>(diary);
        host.Set(selfDrive);
        host.Set<ISelfDriveService>(selfDrive);
        host.Set<ISelfDriveMemo>(selfDrive);

        RegisterTools(host, stickers, images, diary, selfDrive);

        // 状态面板
        host.AddStatus(new StatusProvider("表情库", () => stickers.Snapshot()));
        host.AddStatus(new StatusProvider("日记", () => diary.State()));
        host.AddStatus(new StatusProvider("空间", () => diary.SocialState()));
        host.AddStatus(new StatusProvider("语音", () => voice.EdgeState()));
        host.AddStatus(new StatusProvider("自驱", () => selfDrive.State()));
        host.AddStatus(new StatusProvider("画画", () => images.Snapshot()));
        host.AddStatus(new StatusProvider("兴趣", () => new JsonObject { ["interests"] = surf.ListInterests() }));

        // 测试按钮
        host.AddTest(new TestAction("语音：装 edge-tts", () => voice.InstallEdgeAsync()));
        host.AddTest(new TestAction("语音：试合成一句", async () =>
        {
            var (path, engine, reason) = await voice.SynthesizeAsync("喂喂？测试一下，大肥鱼说话。");
            try { if (path is not null) File.Delete(path); } catch { /* 清理失败无所谓 */ }
            return path is null
                ? new JsonObject { ["ok"] = false, ["message"] = reason }
                : new JsonObject { ["ok"] = true, ["message"] = engine + " 合成成功" };
        }));
        host.AddTest(new TestAction("日记：现在写一篇", () => diary.RunDiaryAsync("手动")));
        host.AddTest(new TestAction("空间：现在逛一圈", () => diary.RunSocialAsync(true)));
        host.AddTest(new TestAction("自驱：现在冲一次", async () =>
        {
            var fired = await selfDrive.FireAsync();
            return fired
                ? new JsonObject { ["ok"] = true, ["message"] = "冲完了" }
                : new JsonObject { ["ok"] = false, ["message"] = "没冲（看日志）" };
        }));
        host.AddTest(new TestAction("冲浪：整理一次兴趣", () => surf.SurfOnceAsync("")));
        host.AddTest(new TestAction("表情包：网上搜几张", async () =>
        {
            var urls = await StickerSrc.SearchAsync("开心", 6);
            return new JsonObject { ["ok"] = urls.Count > 0, ["urls"] = new JsonArray(urls.Select(u => (JsonNode)u).ToArray()) };
        }));

        return media;
    }

    /// <summary>AppHost 每分钟调一次：日记到点写、空间到点逛。</summary>
    public static async Task TickAsync(FeatureHost host, CancellationToken ct)
    {
        if (host.Get<DiaryService>() is not { } diary) return;
        await diary.TickAsync(ct).ConfigureAwait(false);
        await diary.TickSocialAsync(ct).ConfigureAwait(false);
    }

    // ---------- 工具执行函数 ----------

    private static void RegisterTools(
        FeatureHost host, StickerService stickers, ImageGenService images,
        DiaryService diary, SelfDriveService selfDrive)
    {
        if (host.Get<IToolRegistry>() is not { } tools) return;

        tools.Register(ToolSchemas.Define(ToolSchemas.ListStickers, (args, ct) =>
        {
            var query = JsonUtil.Str(args["query"], "").Trim();
            var items = stickers.AllItems();
            if (query.Length > 0)
            {
                var hit = StickerService.MatchSticker(new JsonArray(items.Select(i => (JsonNode)i).ToArray()), query);
                return Task.FromResult(hit is null
                    ? $"库里没有叫「{query}」或差不多的。"
                    : hit.ToJsonString());
            }
            var names = items.Select(i => JsonUtil.Str(i["name"], "")).Where(n => n.Length > 0).ToList();
            return Task.FromResult($"表情库一共 {names.Count} 张：{string.Join("、", names)}");
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.EditSticker, (args, ct) =>
        {
            var sticker = JsonUtil.Str(args["sticker"], "").Trim();
            if (sticker.Length == 0) return Task.FromResult("没说改哪张。");
            var hit = stickers.Resolve(sticker);
            if (hit is not { } found) return Task.FromResult($"库里没有「{sticker}」。");
            var md5 = JsonUtil.Str(found.Item["md5"], "");
            if (JsonUtil.Bool(args["delete"], false))
            {
                var removed = stickers.RemoveAsync(md5).GetAwaiter().GetResult();
                return Task.FromResult(JsonUtil.Bool(removed["ok"], false)
                    ? $"删掉了「{JsonUtil.Str(found.Item["name"], sticker)}」。"
                    : "没删掉，看日志。");
            }
            var newName = JsonUtil.Str(args["new_name"], "").Trim();
            var tags = args["tags"] as JsonArray is { } t && t.Count > 0 ? t.Select(n => n?.ToString() ?? "").ToArray() : null;
            if (newName.Length == 0 && tags is null) return Task.FromResult("既没给新名字也没给新标签。");
            var renamed = stickers.RenameAsync(md5, newName.Length > 0 ? newName : null, tags).GetAwaiter().GetResult();
            return Task.FromResult(JsonUtil.Bool(renamed["ok"], false) ? "改好了。" : "没改成，看日志。");
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.AddSticker, async (args, ct) =>
        {
            var source = JsonUtil.Str(args["source"], "").Trim();
            if (source.Length == 0) return "没给图片地址。";
            byte[] bytes;
            try { bytes = await MediaUtil.FetchBytesAsync(source, ct: ct); }
            catch (Exception ex) { return $"拉图失败：{ex.Message}"; }
            var name = MediaUtil.Slice(JsonUtil.Str(args["name"], "").Trim(), 12);
            var tags = args["tags"] as JsonArray is { } t ? t.Select(n => n?.ToString() ?? "").ToArray() : null;
            var added = await stickers.UploadAsync(bytes, name, tags, "steal", null);
            return JsonUtil.Bool(added["ok"], false)
                ? $"收进表情库了：{JsonUtil.Str(added["name"], name)}"
                : "没收成：" + JsonUtil.Str(added["message"], "看日志");
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.FindStickers, async (args, ct) =>
        {
            var keyword = JsonUtil.Str(args["keyword"], "").Trim();
            if (keyword.Length == 0) return "没说想找什么心情的。";
            var urls = await StickerSrc.SearchAsync(keyword, 6, ct);
            if (urls.Count == 0) return "没搜到合适的，换个小情绪词试试。";
            var lines = urls.Select((u, i) => $"{i + 1}. {u}").ToList();
            return "搜到这几张（挑中哪张就用 web_add_sticker 收进库）：\n" + string.Join('\n', lines);
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.DrawSticker, async (args, ct) =>
        {
            var prompt = JsonUtil.Str(args["prompt"], "").Trim();
            if (prompt.Length == 0) return "没说画什么。";
            if (!images.Enabled()) return "画画功能没开（设置里配好 imageGen 再用）。";
            var name = MediaUtil.Slice(JsonUtil.Str(args["name"], "").Trim(), 12);
            var tags = args["tags"] as JsonArray is { } t ? t.Select(n => n?.ToString() ?? "").ToArray() : null;
            var buf = await images.GenerateAsync($"{ImageGenService.StickerStyle}。{prompt}", ct);
            var added = await stickers.UploadAsync(buf, name, tags, "draw", null);
            return JsonUtil.Bool(added["ok"], false)
                ? $"画好收进表情库了：{JsonUtil.Str(added["name"], name)}"
                : "画是画出来了，收库没收成：" + JsonUtil.Str(added["message"], "看日志");
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.WriteDiary, (args, ct) =>
        {
            var content = JsonUtil.Str(args["content"], "").Trim();
            if (content.Length == 0) return Task.FromResult("没写正文。");
            var visibility = JsonUtil.Str(args["visibility"], "all");
            var entry = diary.PostEntry(content, visibility);
            return Task.FromResult(JsonUtil.Bool(entry["ok"], true)
                ? "发出去了。"
                : "没发出去：" + JsonUtil.Str(entry["message"], "看日志"));
        }));

        tools.Register(ToolSchemas.Define(ToolSchemas.EditMemo, (args, ct) =>
        {
            var action = JsonUtil.Str(args["action"], "read").Trim();
            var text = JsonUtil.Str(args["text"], "");
            var memo = selfDrive.EditMemo(action, text);
            return Task.FromResult(JsonUtil.Bool(memo["ok"], false)
                ? JsonUtil.Str(memo["memo"], "")
                : "便签没动：" + JsonUtil.Str(memo["message"], "看日志"));
        }));

        // 当前聊天里补发一张表情（ChatHub 的工具循环会把 chatId 塞进 args）
        tools.Register(ToolSchemas.Define(ToolSchemas.SendSticker, (args, ct) =>
        {
            var chatId = JsonUtil.Str(args["chatId"], "").Trim();
            var name = JsonUtil.Str(args["name"], "").Trim();
            if (chatId.Length == 0 || name.Length == 0) return Task.FromResult("缺聊天或表情名。");
            if (host.Get<ChatHub>() is not { } hub) return Task.FromResult("聊天还没起来。");
            hub.SendSticker(chatId, name);
            return Task.FromResult($"发了「{name}」。");
        }));

        // web_send_sticker / web_draw_image / web_send_image / web_chat_history / web_send_message
        // 需要聊天上下文，由 ChatHub 的工具循环接线（见 ChatHub 工具循环集成）。
    }

    // ---------- 小实现 ----------

    private sealed class StatusProvider(string key, Func<JsonNode?> read) : IStatusProvider
    {
        public string Key { get; } = key;
        public JsonNode? Snapshot() => read();
    }

    private sealed class TestAction(string name, Func<Task<JsonObject>> run) : ITestAction
    {
        public string Name { get; } = name;
        public Task<JsonObject> RunAsync() => run();
    }
}
