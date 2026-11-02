using System.Text.Json.Nodes;
using AuraChat.Core.Features;

namespace AuraChat.Server.Routes;

/// <summary>语音（对齐原 web/routes/voice.js 的 3 条接口）。</summary>
[RouteModule(50)]
public static class VoiceRoutes
{
    private static int _installing;

    public static void Register(Router r, ApiContext api)
    {
        r.Add("GET", "api/voice/edge", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var voice = api.Get<IVoiceService>();
            if (voice is null) { await HttpUtil.Fail(call.Http, "语音组件没装上"); return; }
            var state = voice.EdgeState();
            state["ok"] = true;
            await HttpUtil.JsonAsync(call.Http, state);
        });

        r.Add("POST", "api/voice/edge", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var voice = api.Get<IVoiceService>();
            if (voice is null) { await HttpUtil.Fail(call.Http, "语音组件没装上"); return; }
            if (Interlocked.Exchange(ref _installing, 1) == 1) { await HttpUtil.Fail(call.Http, "正在装，稍等一下"); return; }
            try
            {
                await HttpUtil.JsonAsync(call.Http, await voice.InstallEdgeAsync(call.Http.RequestAborted));
            }
            catch (Exception ex)
            {
                await HttpUtil.Fail(call.Http, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _installing, 0);
            }
        });

        r.Add("POST", "api/voice/test", async call =>
        {
            if (await api.RequireOwnerAsync(call) is null) return;
            var voice = api.Get<IVoiceService>();
            if (voice is null) { await HttpUtil.Fail(call.Http, "语音组件没装上"); return; }

            var b = await HttpUtil.ReadBodyAsync(call.Http);
            var text = b["text"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(text)) text = "晚安啦，今天也辛苦了，明天见～";

            try
            {
                var (file, engine, reason) = await voice.SynthesizeAsync(text, call.Http.RequestAborted);
                if (file is null || !File.Exists(file))
                {
                    await HttpUtil.Fail(call.Http, "念不出来：" + (reason ?? "不知道为啥"));
                    return;
                }

                var ext = Path.GetExtension(file).ToLowerInvariant();
                var mime = ext switch
                {
                    ".mp3" => "audio/mpeg",
                    ".wav" => "audio/wav",
                    _ => "audio/ogg",
                };
                var bytes = await File.ReadAllBytesAsync(file, call.Http.RequestAborted);
                await HttpUtil.JsonAsync(call.Http, new JsonObject
                {
                    ["ok"] = true,
                    ["engine"] = engine ?? "",
                    ["mime"] = mime,
                    ["data"] = Convert.ToBase64String(bytes),
                });
            }
            catch (Exception ex)
            {
                await HttpUtil.Fail(call.Http, "念不出来：" + ex.Message);
            }
        });
    }
}
