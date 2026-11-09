using System.Text.Json.Nodes;
using AuraChat.Core.Features;
using AuraChat.Core.Media;
using AuraChat.Features.Media;

namespace AuraChat.Server.Routes;

/// <summary>聊天（对齐原 web/routes/chat.js 的 4 条接口）。</summary>
[RouteModule(30)]
public static class ChatRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        // ---------- 拉历史 ----------
        r.Add("GET", "api/messages", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var history = api.Get<IHistoryStore>();
            if (history is null) { await HttpUtil.Fail(call.Http, "聊天记录没装上"); return; }

            var chatId = ApiContext.ChatIdOf(me.Subject);
            var file = HttpUtil.Query(call.Http, "file") ?? "";
            var before = ParseLong(HttpUtil.Query(call.Http, "before"));

            if (file is "last" or "prev")
            {
                var (messages, oldestAt, hasMore, index, total, fileId) = history.PageFile(chatId, file, before);
                await HttpUtil.JsonAsync(call.Http, new JsonObject
                {
                    ["ok"] = true,
                    ["messages"] = messages,
                    ["oldestAt"] = oldestAt,
                    ["hasMore"] = hasMore,
                    ["file"] = new JsonObject { ["index"] = index, ["total"] = total, ["id"] = fileId },
                });
                return;
            }

            var limit = (int)Math.Clamp(ParseLong(HttpUtil.Query(call.Http, "limit"), 30), 1, 100);
            var page = history.Page(chatId, before, limit);
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["messages"] = page.Messages,
                ["oldestAt"] = page.OldestAt,
                ["hasMore"] = page.HasMore,
                ["segment"] = new JsonObject { ["limit"] = limit },
            });
        });

        // ---------- 发言 ----------
        r.Add("POST", "api/messages", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var history = api.Get<IHistoryStore>();
            var hub = api.Get<IChatHub>();
            if (history is null) { await HttpUtil.Fail(call.Http, "聊天记录没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var chatId = ApiContext.ChatIdOf(me.Subject);
            var text = b["text"]?.ToString() ?? "";
            var images = b["images"] as JsonArray ?? new JsonArray();
            var voice = b["voice"] as JsonObject;
            var sticker = b["sticker"]?.ToString() ?? "";
            var replyTo = b["replyTo"] as JsonObject;   // D4 引用：{id, text}（id 是被引用消息 id）
            var name = api.DisplayNameOf(me);

            if (text.Trim().Length == 0 && images.Count == 0 && voice is null && sticker.Length == 0)
            {
                await HttpUtil.Fail(call.Http, "没说啥");
                return;
            }

            // 引用块拼进消息文本（落库即带，各端都能看到）
            if (replyTo is not null)
            {
                var refId = replyTo["id"]?.ToString() ?? "";
                var refText = (replyTo["text"]?.ToString() ?? "").Replace("\n", " ");
                if (refId.Length > 0 || refText.Length > 0)
                    text = $"[引用 {MediaUtil.Slice(refText, 60)}]\n" + text;
            }

            var media = api.Get<MediaStore>();
            var appended = 0;

            // 语音条
            if (voice is not null && !string.IsNullOrEmpty(voice["data"]?.ToString()))
            {
                var bytes = HttpUtil.Base64ToBytes(voice["data"]!.ToString());
                var mime = voice["mime"]?.ToString() ?? "audio/webm";
                var dur = (int)Math.Round(AuraChat.Core.JsonUtil.Num(voice["dur"], 0));
                var (mediaId, size) = media!.Save(chatId, bytes, mime);
                var saved = history.Append(chatId, new JsonObject
                {
                    ["who"] = "user",
                    ["uid"] = me.Subject,
                    ["text"] = $"[语音 · {dur}秒]",
                    ["kind"] = "voice",
                    ["mediaId"] = mediaId,
                    ["mediaMime"] = mime,
                    ["bytes"] = size,
                    ["dur"] = dur,
                });
                api.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = saved.DeepClone() });
                appended++;
            }
            else if (sticker.Length > 0)
            {
                var stickers = api.Get<IStickerService>();
                var name2 = "";
                if (stickers is not null)
                {
                    var list = await stickers.ListAsync();
                    foreach (var item in list["items"] as JsonArray ?? new JsonArray())
                    {
                        if (item?["md5"]?.ToString() == sticker) { name2 = item["name"]?.ToString() ?? ""; break; }
                    }
                }
                var savedSticker = history.Append(chatId, new JsonObject
                {
                    ["who"] = "user",
                    ["uid"] = me.Subject,
                    ["text"] = "[表情]",
                    ["kind"] = "sticker",
                    ["sticker"] = sticker,
                    ["name"] = name2,
                    ["mark"] = "user-sticker",
                });
                api.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = savedSticker.DeepClone() });
                appended++;
            }
            else if (text.Trim().Length > 0)
            {
                var savedText = history.Append(chatId, new JsonObject
                {
                    ["who"] = "user",
                    ["uid"] = me.Subject,
                    ["text"] = text,
                    ["kind"] = "text",
                });
                api.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = savedText.DeepClone() });
                appended++;
            }

            // 图片（可多张）
            foreach (var img in images)
            {
                if (img is not JsonObject io) continue;
                var data = HttpUtil.StripDataUrl(io["data"]?.ToString());
                if (string.IsNullOrEmpty(data)) continue;
                var mime = io["mediaType"]?.ToString() ?? "image/png";
                var (mediaId, size) = media!.Save(chatId, Convert.FromBase64String(data), mime);
                var savedImage = history.Append(chatId, new JsonObject
                {
                    ["who"] = "user",
                    ["uid"] = me.Subject,
                    ["text"] = "[图片]",
                    ["kind"] = "image",
                    ["mediaId"] = mediaId,
                    ["mediaMime"] = mime,
                    ["bytes"] = size,
                    ["mark"] = "user-image",
                });
                api.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = savedImage.DeepClone() });
                appended++;
            }

            if (appended == 0) { await HttpUtil.Fail(call.Http, "没说啥"); return; }

            history.Touch(chatId);

            // v3：落盘 + Touch 都完了才喊「收到了」—— 这条排在本 POST 的 message 回显之后、派发 ChatHub 之前。
            if (api.Flag(true, "enabled"))
                api.Emit("received", new JsonObject { ["chatId"] = chatId, ["receivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

            var message = new UserMessage(chatId, me.Subject, name, text, images, voice is null ? null : new VoiceInput(voice["data"]?.ToString() ?? "", voice["mime"]?.ToString() ?? "audio/webm", AuraChat.Core.JsonUtil.Num(voice["dur"], 0)), sticker);
            if (hub is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await hub.OnUserMessageAsync(message); }
                    catch (Exception ex) { api.Log.Error("处理消息出错：" + ex.Message); }
                });
            }

            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        // ---------- 只发语音 ----------
        r.Add("POST", "api/voice", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var history = api.Get<IHistoryStore>();
            var hub = api.Get<IChatHub>();
            var media = api.Get<MediaStore>();
            if (history is null || media is null) { await HttpUtil.Fail(call.Http, "聊天记录没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var data = HttpUtil.StripDataUrl(b["data"]?.ToString());
            if (string.IsNullOrEmpty(data)) { await HttpUtil.Fail(call.Http, "没说啥"); return; }

            var dur = (int)Math.Round(AuraChat.Core.JsonUtil.Num(b["dur"], 0));
            if (dur < 1 || dur > 60) { await HttpUtil.Fail(call.Http, "语音条要 1 到 60 秒"); return; }

            var mime = b["mime"]?.ToString() ?? "audio/webm";
            var chatId = ApiContext.ChatIdOf(me.Subject);
            var (mediaId, size) = media.Save(chatId, Convert.FromBase64String(data), mime);
            var savedVoice = history.Append(chatId, new JsonObject
            {
                ["who"] = "user",
                ["uid"] = me.Subject,
                ["text"] = $"[语音 · {dur}秒]",
                ["kind"] = "voice",
                ["mediaId"] = mediaId,
                ["mediaMime"] = mime,
                ["bytes"] = size,
                ["dur"] = dur,
            });
            api.Emit("message", new JsonObject { ["chatId"] = chatId, ["message"] = savedVoice.DeepClone() });
            history.Touch(chatId);

            // v3：同 api/messages —— 落盘 + Touch 之后、派发 ChatHub 之前喊一次「收到了」。
            if (api.Flag(true, "enabled"))
                api.Emit("received", new JsonObject { ["chatId"] = chatId, ["receivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

            if (hub is not null)
            {
                var message = new UserMessage(chatId, me.Subject, api.DisplayNameOf(me), "", new JsonArray(), new VoiceInput(data, mime, dur), "");
                _ = Task.Run(async () =>
                {
                    try { await hub.OnUserMessageAsync(message); }
                    catch (Exception ex) { api.Log.Error("处理语音出错：" + ex.Message); }
                });
            }
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        // ---------- 正在输入 ----------
        r.Add("POST", "api/typing", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var typing = AuraChat.Core.JsonUtil.Bool(b["typing"], false);
            api.Get<IChatHub>()?.NotifyTyping(ApiContext.ChatIdOf(me.Subject), typing);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        // ---------- 已读回执（D3 / v3）----------
        r.Add("POST", "api/read", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var history = api.Get<IHistoryStore>();
            if (history is null) { await HttpUtil.Fail(call.Http, "聊天记录没装上"); return; }

            var chatId = ApiContext.ChatIdOf(me.Subject);
            // v3：可选 {upToT} —— 客户端报「我看到了这个时间点之前的」，缺省＝现在；
            // 服务端只前推不回退，且夹在服务器现在之内（客户端时钟可能歪）。
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            long? upToT = long.TryParse(b["upToT"]?.ToString(), out var t) && t > 0 ? t : null;
            var (unreadBefore, readAt) = await history.MarkReadAsync(chatId, upToT);

            // 广播给对端：她/主人能看到"对方看到了"，水位＝真正推进后的值
            api.Emit("read", new JsonObject
            {
                ["chatId"] = chatId,
                ["subject"] = me.Subject,
                ["readAt"] = readAt,
            });
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["myReadAt"] = readAt,
                ["unreadBefore"] = unreadBefore,
            });
        });

        // 自己的已读水位（渲染"已读/未读"角标用）。v3：只返 myReadAt，不再给对端的（那是不对称信息，前端也不该推断）
        r.Add("GET", "api/read", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var history = api.Get<IHistoryStore>();
            if (history is null) { await HttpUtil.Fail(call.Http, "聊天记录没装上"); return; }
            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["myReadAt"] = history.LastReadAt(ApiContext.ChatIdOf(me.Subject)),
            });
        });
    }

    private static long ParseLong(string? s, long fallback = 0)
        => long.TryParse(s, out var v) ? v : fallback;
}
