using System.Text.Json.Nodes;
using AuraChat.Core.Features;
using AuraChat.Core.Media;
using Microsoft.AspNetCore.Http;

namespace AuraChat.Server.Routes;

/// <summary>
/// 媒体 / 表情 / 头像 / 背景（对齐原 web/routes/media.js 的 11 条接口）。
/// 网页端用 Authorization 头取回媒体再渲染（blob URL），凭证不再进 URL；
/// 旧版 &lt;img&gt;/&lt;audio&gt; 直连用的 ?token= 只是有界兼容兜底，命中就打日志提醒迁移。
/// </summary>
[RouteModule(40)]
public static class MediaRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        // ---------- 聊天里的图片 / 语音 ----------
        r.Prefix("GET", "api/media/", async call =>
        {
            var me = MeOf(api, call.Http);
            if (me is null) { await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"没登录"}""", "application/json; charset=utf-8"); return; }

            var rest = call.Path["api/media/".Length..];
            var id = Uri.UnescapeDataString(rest.Split('/')[0]);
            if (string.IsNullOrEmpty(id)) { await HttpUtil.SendAsync(call.Http, 404, """{"ok":false,"reason":"没有这个媒体"}""", "application/json; charset=utf-8"); return; }

            var media = api.Get<MediaStore>();
            var path = media?.PathOf(ApiContext.ChatIdOf(me.Subject), id);
            if (path is null)
            {
                await HttpUtil.SendAsync(call.Http, 404, """{"ok":false,"reason":"这个媒体已经过期了"}""", "application/json; charset=utf-8");
                return;
            }

            var ext = MediaStore.ExtOfMediaId(id);
            var bytes = await File.ReadAllBytesAsync(path, call.Http.RequestAborted);
            await HttpUtil.SendBytesAsync(call.Http, bytes, MediaStore.MimeOfExt(ext),
                "private, max-age=31536000, immutable", "\"" + id + "\"");
        });

        // ---------- 表情包文件 ----------
        // 与列表/聊天一致要求登录：表情库属于站点的个人资产，不再是无凭证公开资源
        r.Prefix("GET", "api/sticker/", async call =>
        {
            var me = MeOf(api, call.Http);
            if (me is null) { await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"没登录"}""", "application/json; charset=utf-8"); return; }
            var stickers = api.Get<IStickerService>();
            if (stickers is null) { await HttpUtil.Fail(call.Http, "表情库没装上"); return; }

            var md5 = Uri.UnescapeDataString(call.Path["api/sticker/".Length..].Split('/')[0]);
            var path = stickers.PathOf(md5);
            if (path is null) { await HttpUtil.Fail(call.Http, "没有这张表情"); return; }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/png",
            };
            var bytes = await File.ReadAllBytesAsync(path, call.Http.RequestAborted);
            await HttpUtil.SendBytesAsync(call.Http, bytes, mime, "public, max-age=86400", "\"" + md5 + "\"");
        });

        r.Add("GET", "api/stickers", async call =>
        {
            if (await api.RequireUserAsync(call) is null) return;
            var stickers = api.Get<IStickerService>();
            if (stickers is null) { await HttpUtil.Fail(call.Http, "表情库没装上"); return; }
            await HttpUtil.JsonAsync(call.Http, await stickers.ListAsync());
        });

        r.Add("POST", "api/stickers/upload", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var stickers = api.Get<IStickerService>();
            if (stickers is null) { await HttpUtil.Fail(call.Http, "表情库没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var from = b["from"]?.ToString() ?? "";
            if (from == "steal" && api.Flag(true, "stickers", "steal") == false) { await HttpUtil.Fail(call.Http, "偷表情开关关着"); return; }

            var data = HttpUtil.StripDataUrl(b["data"]?.ToString());
            if (string.IsNullOrEmpty(data)) { await HttpUtil.Fail(call.Http, "没有图片数据"); return; }

            var tags = b["tags"] is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0) : null;
            var result = await stickers.UploadAsync(Convert.FromBase64String(data), b["name"]?.ToString(), tags, from, b["hint"]?.ToString());
            await HttpUtil.JsonAsync(call.Http, result);
        });

        r.Prefix("DELETE", "api/stickers/", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var stickers = api.Get<IStickerService>();
            if (stickers is null) { await HttpUtil.Fail(call.Http, "表情库没装上"); return; }
            var md5 = Uri.UnescapeDataString(call.Path["api/stickers/".Length..]);
            await HttpUtil.JsonAsync(call.Http, await stickers.RemoveAsync(md5));
        });

        r.Prefix("PATCH", "api/stickers/", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var stickers = api.Get<IStickerService>();
            if (stickers is null) { await HttpUtil.Fail(call.Http, "表情库没装上"); return; }
            var md5 = Uri.UnescapeDataString(call.Path["api/stickers/".Length..]);
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var tags = b["tags"] is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0) : null;
            await HttpUtil.JsonAsync(call.Http, await stickers.RenameAsync(md5, b["name"]?.ToString(), tags));
        });

        // ---------- 站点图标 / 头像 / 背景 ----------
        // 图标是站点品牌入口（登录前可用），只给公共 icon.svg，不再回退成主人的私人头像文件
        r.Add("GET", "api/icon", async call =>
        {
            var icon = Path.Combine(api.PublicRoot, "icon.svg");
            if (!File.Exists(icon)) { await HttpUtil.SendAsync(call.Http, 404, "not found"); return; }
            await HttpUtil.SendBytesAsync(call.Http, await File.ReadAllBytesAsync(icon), "image/svg+xml", "no-cache");
        });

        // 头像/背景是主人的个人资源，和聊天媒体一样要登录后才能取（网页端用 Bearer fetch 成 blob 渲染）
        r.Add("GET", "api/avatar", async call =>
        {
            if (MeOf(api, call.Http) is null) { await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"没登录"}""", "application/json; charset=utf-8"); return; }
            var avatar = DataFile(api, "avatar");
            if (!File.Exists(avatar)) { await HttpUtil.Fail(call.Http, "还没有头像"); return; }
            var mime = ReadMime(api, "avatar.mime", "image/png");
            await HttpUtil.SendBytesAsync(call.Http, await File.ReadAllBytesAsync(avatar), mime, "no-store");
        });

        r.Add("POST", "api/avatar", async call =>
        {
            var me = await api.RequireOwnerAsync(call);
            if (me is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var data = HttpUtil.StripDataUrl(b["data"]?.ToString());
            if (string.IsNullOrEmpty(data))
            {
                TryDelete(DataFile(api, "avatar"));
                TryDelete(DataFile(api, "avatar.mime"));
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
                return;
            }
            await File.WriteAllBytesAsync(DataFile(api, "avatar"), Convert.FromBase64String(data));
            await File.WriteAllTextAsync(DataFile(api, "avatar.mime"), b["mime"]?.ToString() ?? "image/png");
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });

        r.Add("GET", "api/background", async call =>
        {
            if (MeOf(api, call.Http) is null) { await HttpUtil.SendAsync(call.Http, 401, """{"ok":false,"reason":"没登录"}""", "application/json; charset=utf-8"); return; }
            var bg = DataFile(api, "background");
            if (!File.Exists(bg)) { await HttpUtil.Fail(call.Http, "还没有背景图"); return; }
            var mime = ReadMime(api, "background.mime", "image/jpeg");
            await HttpUtil.SendBytesAsync(call.Http, await File.ReadAllBytesAsync(bg), mime, "no-store");
        });

        r.Add("POST", "api/background", async call =>
        {
            var me = await api.RequireOwnerAsync(call);
            if (me is null) return;
            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var data = HttpUtil.StripDataUrl(b["data"]?.ToString());
            if (string.IsNullOrEmpty(data))
            {
                TryDelete(DataFile(api, "background"));
                TryDelete(DataFile(api, "background.mime"));
                api.Config.MergeFromPanel(new JsonObject { ["theme"] = new JsonObject { ["background"] = "" } });
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
                return;
            }
            await File.WriteAllBytesAsync(DataFile(api, "background"), Convert.FromBase64String(data));
            await File.WriteAllTextAsync(DataFile(api, "background.mime"), b["mime"]?.ToString() ?? "image/jpeg");
            api.Config.MergeFromPanel(new JsonObject { ["theme"] = new JsonObject { ["background"] = "local" } });
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true });
        });
    }

    /// <summary>凭证 header/cookie 优先；旧版 URL ?token= 只是有界兼容兜底，命中打日志（原版 meOf）。</summary>
    private static Core.Auth.SessionUser? MeOf(ApiContext api, HttpContext ctx)
    {
        var (token, fromQuery) = HttpUtil.SessionTokenOf(ctx);
        if (fromQuery) api.Log.Child("web").Warn("媒体请求用了旧版 URL ?token= 兼容，请更新客户端改用 Authorization");
        return api.Auth.SessionUserOf(token);
    }

    private static string DataFile(ApiContext api, string name) => Path.Combine(api.Store.Root, name);

    private static string ReadMime(ApiContext api, string name, string fallback)
    {
        try
        {
            var text = File.ReadAllText(DataFile(api, name)).Trim();
            return string.IsNullOrEmpty(text) ? fallback : text;
        }
        catch { return fallback; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就算了 */ }
    }
}
