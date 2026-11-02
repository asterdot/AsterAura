using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Media;

/// <summary>
/// 自驱（逐条照原 features/selfdrive.js）：没人喊她时，自己按 intervalMinutes 找点事做——
/// 看便签、画表情、冲浪，然后把想说的话推给主人。静默时段不打扰，每天最多 maxPerDay 次。
/// </summary>
public sealed class SelfDriveService : ISelfDriveService
{
    /// <summary>原版默认便签。</summary>
    public const string DefaultMemo = "# 便签\n\n- 看看用户正在干什么\n- 想找个由头夸夸用户";

    /// <summary>自画表情的 14 种心情（原 DRAW_MOODS）。</summary>
    public static readonly string[] DrawMoods =
    [
        "开心", "有点委屈", "生气", "无语", "困了", "害羞", "得意",
        "难过", "好奇", "撒娇", "吃醋", "发呆", "被夸了很高兴", "不想理你",
    ];

    private const int MemoReadLimit = 8000;
    private const int MemoWriteLimit = 20000;
    private const int StickerLibrarySoftCap = 60;

    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly StickerService _stickers;
    private readonly ImageGenService _images;
    private readonly SurfService? _surf;

    private int _drawCount;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SelfDriveService(FeatureHost host, StickerService stickers, ImageGenService images, SurfService? surf)
    {
        _host = host;
        _log = host.Log.Child("selfdrive");
        _stickers = stickers;
        _images = images;
        _surf = surf;
    }

    /// <summary>把自驱想说的话推给主人（原 hub.pushToSession，聊天层接上后才有效）。</summary>
    public Func<string, CancellationToken, Task>? PushToOwner { get; set; }

    /// <summary>每分钟额外轮询一次（MediaModule 接日记 / 空间互动）。</summary>
    public Func<CancellationToken, Task>? OnMinute { get; set; }

    private string MemoFile => _host.Store.PathIn("selfdrive.md");
    private string StateFile => _host.Store.PathIn("selfdrive.json");
    private string OwnerName => _host.Text("主人", "ownerName");

    // ---------------- 便签（ISelfDriveMemo） ----------------

    public string ReadMemo()
    {
        try
        {
            if (!File.Exists(MemoFile))
            {
                _host.Store.EnsureDir(_host.Store.DataDir);
                File.WriteAllText(MemoFile, DefaultMemo + "\n");
                return DefaultMemo;
            }
            var text = File.ReadAllText(MemoFile).Trim();
            return MediaUtil.Slice(text, MemoReadLimit);
        }
        catch (Exception ex)
        {
            _log.Warn("读便签失败：", ex.Message);
            return "";
        }
    }

    public JsonObject WriteMemo(string? text)
    {
        try
        {
            _host.Store.EnsureDir(_host.Store.DataDir);
            File.WriteAllText(MemoFile, MediaUtil.Slice(text ?? "", MemoWriteLimit));
            return new JsonObject { ["ok"] = true };
        }
        catch (Exception ex)
        {
            _log.Warn("写便签失败：", ex.Message);
            return new JsonObject { ["ok"] = false, ["message"] = ex.Message };
        }
    }

    /// <summary>给工具用：按 action 读 / 追加 / 替换便签（原 web_edit_memo）。</summary>
    public JsonObject EditMemo(string? action, string? text)
    {
        var act = (action ?? "").Trim();
        if (act == "read")
        {
            var memo = ReadMemo().Trim();
            return new JsonObject { ["ok"] = true, ["text"] = memo.Length == 0 ? "便签还是空的" : memo };
        }

        if (act == "append")
        {
            var body = (text ?? "").Trim();
            if (body.Length == 0) return Fail("要说添什么");
            var cur = ReadMemo().Trim();
            var next = cur.Length > 0 ? $"{cur}\n- {body}" : $"# 便签\n\n- {body}";
            var written = WriteMemo(next);
            return JsonUtil.Bool(written["ok"], false) ? Ok("便签加上了") : Fail($"便签没写成：{written["message"]}");
        }

        if (act == "replace")
        {
            var body = (text ?? "").Trim();
            if (body.Length == 0) return Fail("要说换成什么");
            var written = WriteMemo(body);
            return JsonUtil.Bool(written["ok"], false) ? Ok("便签换好了") : Fail($"便签没写成：{written["message"]}");
        }

        return Fail("action 只有 read / append / replace");
    }

    // ---------------- 状态 ----------------

    private JsonObject LoadState()
        => DataStore.ReadObject(StateFile, new JsonObject
        {
            ["day"] = "",
            ["count"] = 0,
            ["nextAt"] = 0,
            ["last"] = 0,
        });

    private void SaveState(JsonObject state) => DataStore.WriteJson(StateFile, state);

    public JsonObject State()
    {
        var state = LoadState();
        var today = MediaUtil.DayKey();
        if (state["day"]?.ToString() != today) state["count"] = 0;
        return new JsonObject
        {
            ["day"] = state["day"]?.ToString() ?? "",
            ["count"] = JsonUtil.Int(state["count"], 0),
            ["nextAt"] = JsonUtil.Num(state["nextAt"], 0),
            ["last"] = JsonUtil.Num(state["last"], 0),
            ["enabled"] = _host.Flag(false, "selfDrive", "enabled"),
            ["intervalMinutes"] = _host.Int(60, "selfDrive", "intervalMinutes"),
            ["maxPerDay"] = _host.Int(4, "selfDrive", "maxPerDay"),
        };
    }

    // ---------------- 自驱一轮 ----------------

    /// <summary>手动触发一次（原 selfDrive.fire），返回有没有发出话。</summary>
    public async Task<bool> FireAsync(CancellationToken ct = default)
    {
        var memo = ReadMemo();
        var drew = await MaybeDrawAsync(ct);

        var surfBlock = "";
        if (_surf is not null)
        {
            try
            {
                var saw = await _surf.SurfOnceAsync(memo, ct);
                if (JsonUtil.Bool(saw["ok"], false))
                {
                    var topic = saw["topic"]?.ToString() ?? "";
                    var digest = saw["digest"]?.ToString() ?? "";
                    surfBlock = $"\n\n【你刚才自己上网看到的】你在看「{topic}」，看完的印象：\n{digest}";
                    _log.Info($"自驱：冲浪看了「{topic}」");
                }
                else
                {
                    _log.Info($"自驱：没冲成浪（{saw["message"]?.ToString() ?? ""}）");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("冲浪失败：", ex.Message);
            }
        }

        if (string.IsNullOrEmpty(memo) && surfBlock.Length == 0 && drew.Length == 0) return false;

        var parts = new List<string>
        {
            $"【系统提醒，不是{OwnerName}说的话】没人喊你，是你自己在往下过。",
        };
        if (!string.IsNullOrEmpty(memo)) parts.Add($"你和{OwnerName}的共享便签：\n{memo}");
        if (drew.Length > 0) parts.Add(drew);
        if (surfBlock.Length > 0) parts.Add(surfBlock);

        parts.Add(surfBlock.Length > 0
            ? "你刚看完这些，想分享就自然地跟她/他说——像刚刷到什么顺手发一句那样，不是汇报「我上了网」。也可以顺着便签说别的。"
            : "看着便签自己找点由头：关心一下、接着说上次没说完的、或者忽然想起件小事。便签不是待办清单，不用逐条打勾，也别汇报你「做了什么」。");
        parts.Add("用一两句话开口就好，别解释这是系统提醒；实在没什么想说的就只回 [沉默]。");

        var prompt = string.Join("\n", parts.Where(part => part.Length > 0));

        if (PushToOwner is null)
        {
            _log.Warn("自驱：聊天层还没接上推送，这轮跳过");
            return false;
        }

        await PushToOwner(prompt, ct);
        return true;
    }

    /// <summary>隔几次自己画张表情（原 maybeDraw），返回要拼进提示词的那段话。</summary>
    private async Task<string> MaybeDrawAsync(CancellationToken ct)
    {
        if (!_host.Flag(false, "imageGen", "enabled")) return "";
        if (!_host.Flag(true, "stickers", "selfDraw")) return "";
        if (_stickers.Count >= StickerLibrarySoftCap) return "";

        _drawCount++;
        var every = _host.Int(3, "stickers", "drawEvery");
        if (every <= 0) every = 3;
        if (_drawCount % every != 0) return "";

        var mood = DrawMoods[Random.Shared.Next(DrawMoods.Length)];
        try
        {
            var buf = await _images.GenerateAsync($"{ImageGenService.StickerStyle}。{mood}的表情", ct);
            var result = await _stickers.UploadAsync(
                buf,
                MediaUtil.Slice(mood, 12),
                [mood],
                "draw",
                $"小肥鱼自己画的表情：{mood}");

            if (JsonUtil.Bool(result["ok"], false))
            {
                var name = result["item"]?["name"]?.ToString() ?? mood;
                _log.Info($"自己画了张表情「{name}」");
                return $"\n\n【你刚才自己画了张表情】「{name}」，已经收进你的表情库了。";
            }
            _log.Warn("自己画表情失败：", result["reason"]?.ToString() ?? "未知原因");
        }
        catch (Exception ex)
        {
            _log.Warn("自己画表情失败：", ex.Message);
        }
        return "";
    }

    /// <summary>分钟轮询（原 selfDrive.tick）：开关、跨天、静默时段、间隔与每日上限。</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!_host.Flag(true, "enabled") || !_host.Flag(false, "selfDrive", "enabled")) return;

        var now = DateTime.Now;
        var state = LoadState();
        var today = MediaUtil.DayKey(now);
        if (state["day"]?.ToString() != today)
        {
            state["day"] = today;
            state["count"] = 0;
            state["nextAt"] = 0;
        }

        var quietFrom = _host.Text("01:00", "selfDrive", "quietFrom");
        var quietTo = _host.Text("08:00", "selfDrive", "quietTo");
        if (MediaUtil.InRange(now, quietFrom, quietTo))
        {
            SaveState(state);
            return;
        }

        var intervalMs = (long)_host.Int(60, "selfDrive", "intervalMinutes") * 60_000L;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nextAt = (long)JsonUtil.Num(state["nextAt"], 0);
        if (nextAt <= 0)
        {
            state["nextAt"] = nowMs + intervalMs;
            nextAt = nowMs + intervalMs;
        }

        var count = JsonUtil.Int(state["count"], 0);
        if (count >= _host.Int(4, "selfDrive", "maxPerDay") || nowMs < nextAt)
        {
            SaveState(state);
            return;
        }

        state["count"] = count + 1;
        state["last"] = nowMs;
        state["nextAt"] = nowMs + intervalMs;
        SaveState(state);

        try
        {
            await FireAsync(ct);
        }
        catch (Exception ex)
        {
            _log.Warn("自驱失败：", ex.Message);
        }
    }

    // ---------------- 定时器 ----------------

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is { IsCompleted: false }) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        _log.Info("自驱服务已启动（每分钟看一次）");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        try { cts?.Cancel(); } catch { }
        if (loop is not null)
        {
            try { await loop; } catch { }
        }
        cts?.Dispose();
        _log.Info("自驱服务已停");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (OnMinute is not null)
                {
                    try { await OnMinute(ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn("分钟轮询失败：", ex.Message); }
                }

                try { await TickAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn("自驱轮询失败：", ex.Message); }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private static JsonObject Ok(string message) => new() { ["ok"] = true, ["message"] = message };

    private static JsonObject Fail(string message) => new() { ["ok"] = false, ["message"] = message };
}
