using System.Text.Json.Nodes;
using AuraChat.Core.Features;

namespace AuraChat.Server.Routes;

/// <summary>空间 / 日记 / 留言（对齐原 web/routes/azone.js 的 7 条接口）。</summary>
[RouteModule(70)]
public static class AzoneRoutes
{
    public static void Register(Router r, ApiContext api)
    {
        r.Add("GET", "api/diary", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            await HttpUtil.JsonAsync(call.Http, new JsonObject
            {
                ["ok"] = true,
                ["entries"] = diary.VisibleEntries(me.Role == "owner"),
                ["state"] = diary.State(),
                ["stats"] = diary.Stats(),
            });
        });

        r.Add("POST", "api/diary/comment", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var result = diary.AddComment(
                b["entryId"]?.ToString() ?? "",
                me.Subject,
                api.DisplayNameOf(me),
                b["text"]?.ToString()?.Trim() ?? "");

            // 留言后让她去看一眼（原版同款 fire-and-forget）
            _ = Task.Run(async () =>
            {
                try { await diary.RunSocialAsync(); }
                catch (Exception ex) { api.Log.Warn("回访留言出错：" + ex.Message); }
            });

            await HttpUtil.JsonAsync(call.Http, result);
        });

        r.Add("POST", "api/diary/comment/delete", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var entryId = b["entryId"]?.ToString() ?? "";
            var commentId = b["commentId"]?.ToString() ?? "";
            var replyId = b["replyId"]?.ToString() ?? "";

            var comment = diary.CommentOf(entryId, commentId);
            if (comment is null) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "这条留言没了" }); return; }

            if (replyId.Length > 0)
            {
                var reply = diary.ReplyOf(entryId, commentId, replyId);
                if (reply is null) { await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "这条回复没了" }); return; }
                if (me.Role != "owner" && (reply["from"]?.ToString() ?? "") != me.Subject)
                {
                    await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "只能删自己的" });
                    return;
                }
                await HttpUtil.JsonAsync(call.Http, diary.DeleteReply(entryId, commentId, replyId));
                return;
            }

            if (me.Role != "owner" && (comment["from"]?.ToString() ?? "") != me.Subject)
            {
                await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = false, ["reason"] = "只能删自己的" });
                return;
            }
            await HttpUtil.JsonAsync(call.Http, diary.DeleteComment(entryId, commentId));
        });

        r.Add("POST", "api/diary/reply", async call =>
        {
            var me = await api.RequireUserAsync(call);
            if (me is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var result = diary.AddReply(
                b["entryId"]?.ToString() ?? "",
                b["commentId"]?.ToString() ?? "",
                me.Subject,
                api.DisplayNameOf(me),
                b["text"]?.ToString()?.Trim() ?? "");

            _ = Task.Run(async () =>
            {
                try { await diary.RunSocialAsync(); }
                catch (Exception ex) { api.Log.Warn("回访留言出错：" + ex.Message); }
            });

            await HttpUtil.JsonAsync(call.Http, result);
        });

        r.Add("POST", "api/diary/run", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var visibility = b["visibility"]?.ToString() == "self" ? "self" : "all";
            var entry = await diary.WriteAsync(visibility, call.Http.RequestAborted);
            await HttpUtil.JsonAsync(call.Http, new JsonObject { ["ok"] = true, ["entry"] = entry });
        });

        r.Add("POST", "api/diary/delete", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            await HttpUtil.JsonAsync(call.Http, diary.Remove(b["entryId"]?.ToString() ?? ""));
        });

        r.Add("POST", "api/diary/social-run", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var diary = api.Get<IDiaryService>();
            if (diary is null) { await HttpUtil.Fail(call.Http, "空间模块没装上"); return; }

            var result = await diary.RunSocialAsync(call.Http.RequestAborted);
            result["ok"] = true;
            await HttpUtil.JsonAsync(call.Http, result);
        });
    }
}
