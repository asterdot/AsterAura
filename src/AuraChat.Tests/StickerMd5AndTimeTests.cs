using System.Reflection;
using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Chat;
using AuraChat.Features.Media;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// #3 时间观念：她系统提示里必须真的带着「现在几点」这一行（光有 check_clock 工具不算 —— 模型不会每轮都主动调）。
/// </summary>
public class TimeSenseTests : IDisposable
{
    private readonly string _root;
    private readonly FeatureHost _host;

    public TimeSenseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-time-tests-" + Guid.NewGuid().ToString("N"));
        var store = new DataStore(_root);
        var log = new AppLog();
        var config = new ConfigStore(store, log).Load();
        _host = new FeatureHost
        {
            Store = store,
            Config = config,
            Auth = new AuthService(store, log),
            Log = log,
            Persona = new PersonaService(store, Path.Combine(_root, "personas-bundled")),
        };
    }

    [Fact]
    public void ClockText_FullFormat_DateWeekdayTimePeriod()
    {
        // 2026-11-04 是周三
        var text = ChatHub.ClockText(new DateTime(2026, 11, 4, 18, 2, 0));
        Assert.Equal("2026-11-04 周三 18:02（晚上，18 点）", text);
    }

    [Theory]
    // 时段边界。旧实现 0-4 点会落进「上午」分支（switch 顺序 bug），这批必须按新口径钉死。
    [InlineData(0, "深夜")]
    [InlineData(4, "深夜")]
    [InlineData(5, "清晨")]
    [InlineData(7, "清晨")]
    [InlineData(8, "上午")]
    [InlineData(10, "上午")]
    [InlineData(11, "中午")]
    [InlineData(12, "中午")]
    [InlineData(13, "下午")]
    [InlineData(17, "下午")]
    [InlineData(18, "晚上")]
    [InlineData(22, "晚上")]
    [InlineData(23, "深夜")]
    public void ClockText_PeriodBoundaries(int hour, string period)
    {
        var text = ChatHub.ClockText(new DateTime(2026, 11, 4, hour, 30, 0));
        Assert.Contains($"（{period}，{hour} 点）", text);
    }

    [Fact]
    public void NowLine_IsNowMarkerPlusClockText()
    {
        var t = new DateTime(2026, 11, 4, 7, 5, 0);
        Assert.Equal("【现在】" + ChatHub.ClockText(t), ChatHub.NowLine(t));
        Assert.StartsWith("【现在】2026-11-04 周三 07:05", ChatHub.NowLine(t));
    }

    [Fact]
    public async Task SystemPrompt_CarriesNowLine()
    {
        // 端到端：拼出来的系统提示里必须有【现在】这一行 —— 这才是「她有时间观念」的落点。
        var hub = new ChatHub(_host);
        var pendingType = typeof(ChatHub).GetNestedType("Pending", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType)!;
        pendingType.GetProperty("ChatId")!.SetValue(pending, "owner");
        pendingType.GetProperty("UserId")!.SetValue(pending, "owner");
        pendingType.GetProperty("Nickname")!.SetValue(pending, "测试");

        var method = typeof(ChatHub).GetMethod("BuildSystemPromptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<string>)method.Invoke(hub, [pending])!;
        var prompt = await task;

        Assert.Contains("【现在】", prompt);
        Assert.Matches(@"【现在】\d{4}-\d{2}-\d{2} 周[日一二三四五六] \d{2}:\d{2}（", prompt);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

/// <summary>
/// #4 断根：她自动配/挑的表情落库必须是 md5 —— 存成 manual-&lt;md5&gt;.jpg 文件名时
/// 客户端拼出的 /api/sticker/manual-xxx.jpg 认不出索引 → 200+JSON → 破图。
/// </summary>
public class StickerMd5Tests : IDisposable
{
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _root;
    private readonly FeatureHost _host;
    private readonly StickerService _stickers;
    private readonly HistoryStore _history;
    private readonly string _chatId = "owner";

    public StickerMd5Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-sticker-md5-" + Guid.NewGuid().ToString("N"));
        var store = new DataStore(_root);
        var log = new AppLog();
        var config = new ConfigStore(store, log).Load();
        _host = new FeatureHost
        {
            Store = store,
            Config = config,
            Auth = new AuthService(store, log),
            Log = log,
            Persona = new PersonaService(store, Path.Combine(_root, "personas-bundled")),
        };
        var media = new MediaStore(store, log);
        _history = new HistoryStore(_host, media);
        _host.Set<MediaStore>(media);
        _host.Set<IHistoryStore>(_history);
        _stickers = new StickerService(_host);
        _host.Set<IStickerService>(_stickers);
    }

    private string UploadOne()
    {
        var r = _stickers.UploadAsync(Convert.FromBase64String(Png1x1), "测试表情", ["测试"], "manual", null)
            .GetAwaiter().GetResult();
        Assert.True((bool?)r["ok"] ?? false, "测试表情入库失败");
        return r["item"]!["md5"]!.ToString();
    }

    [Fact]
    public async Task PickAsync_ReturnsMd5_NotFileName()
    {
        var md5 = UploadOne();
        // autoChance 拉满，绕开随机闸（闸挡掉时返回 null，不算断根失败）
        _host.Config.Save(new JsonObject { ["stickers"] = new JsonObject { ["enabled"] = true, ["autoChance"] = 1.0 } });

        var picked = await _stickers.PickAsync("开心");
        Assert.NotNull(picked);
        Assert.Matches("^[0-9a-f]{32}$", picked);          // 是 md5
        Assert.DoesNotContain("manual-", picked);           // 不是 manual-xxx.jpg
        Assert.NotNull(_stickers.PathOf(picked));          // /api/sticker/<值> 能真取到图
        Assert.Equal(md5, picked);
    }

    [Fact]
    public void RandomSticker_ReturnsMd5_NotFileName()
    {
        var md5 = UploadOne();
        var picked = _stickers.RandomSticker();
        Assert.NotNull(picked);
        Assert.Matches("^[0-9a-f]{32}$", picked);
        Assert.NotNull(_stickers.PathOf(picked));
        Assert.Equal(md5, picked);
    }

    [Fact]
    public void SendSticker_Md5Input_StoresMd5InHistory()
    {
        var md5 = UploadOne();
        var hub = new ChatHub(_host);

        hub.SendStickerForTest(_chatId, md5);   // 她自己挑好的那张（ReplyItem 走的就是这条）

        var last = _history.Last(_chatId);
        Assert.NotNull(last);
        var stored = last!["sticker"]!.ToString();
        Assert.Equal(md5, stored);
        Assert.Matches("^[0-9a-f]{32}$", stored);
        Assert.NotNull(_stickers.PathOf(stored));           // 断根的核心断言：存的值必须能换到图
        Assert.Equal("测试表情", last["name"]!.ToString()); // name 是真名，不是 md5/文件名
    }

    [Fact]
    public void SendSticker_TagInput_StoresMd5InHistory()
    {
        var md5 = UploadOne();
        var hub = new ChatHub(_host);

        hub.SendStickerForTest(_chatId, "开心");  // 她写 <sticker:开心>（标签路径）

        var last = _history.Last(_chatId);
        Assert.NotNull(last);
        var stored = last!["sticker"]!.ToString();
        Assert.Matches("^[0-9a-f]{32}$", stored);           // 两条路径落库都必须是 md5
        Assert.NotNull(_stickers.PathOf(stored));
        Assert.Equal(md5, stored);                          // 库里只有一张，挑到的只能是它
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
