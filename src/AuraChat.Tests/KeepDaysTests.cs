using System.Text.Json.Nodes;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using AuraChat.Features.Chat;
using Xunit;

namespace AuraChat.Tests;

/// <summary>T1 红绿测试：聊天文本永不按天删，keepDays 只管媒体，0 = 永不清理。</summary>
public class KeepDaysTests : IDisposable
{
    private readonly string _root;
    private readonly DataStore _store;
    private readonly ConfigStore _config;
    private readonly FeatureHost _host;
    private readonly HistoryStore _history;
    private readonly MediaStore _media;
    private readonly string _chatId = "test-chat";

    public KeepDaysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurachat-tests-" + Guid.NewGuid().ToString("N"));
        _store = new DataStore(_root);
        var log = new AppLog();
        _config = new ConfigStore(_store, log).Load();
        var auth = new AuthService(_store, log);
        var persona = new PersonaService(_store, Path.Combine(_root, "personas-bundled"));
        _host = new FeatureHost { Store = _store, Config = _config, Auth = auth, Log = log, Persona = persona };
        _media = new MediaStore(_store, log);
        _history = new HistoryStore(_host, _media);
    }

    private long Now => HistoryStoreUtcNow();

    internal static long HistoryStoreUtcNow() =>
        new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();

    /// <summary>造一个 8 天前的旧分片：一条带媒体的消息 + 对应媒体文件（媒体落盘时间也是 8 天前）。</summary>
    private (string SegId, string MediaId, string MediaPath, long OldT) SeedOldSegment()
    {
        var oldT = Now - 8L * 86_400_000;
        var segId = $"seg-001-{oldT.ToString("x")}-abc123";
        var mediaId = Guid.NewGuid().ToString("D") + ".png";
        var mediaPath = Path.Combine(_media.DirOf(_chatId), mediaId);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        File.WriteAllBytes(mediaPath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(mediaPath, DateTimeOffset.FromUnixTimeMilliseconds(oldT).UtcDateTime);

        var seg = new JsonObject
        {
            ["chatId"] = _chatId,
            ["id"] = segId,
            ["from"] = oldT,
            ["to"] = oldT,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["id"] = "m-old-1",
                ["t"] = oldT,
                ["role"] = "user",
                ["text"] = "旧消息，谁也不许删我",
                ["mediaId"] = mediaId,
            }),
        };
        DataStore.WriteJson(Path.Combine(_store.DataDir, "chats", _chatId, segId + ".json"), seg);
        DataStore.WriteJson(Path.Combine(_store.DataDir, "chats", _chatId, "index.json"), new JsonObject
        {
            ["chatId"] = _chatId,
            ["segments"] = new JsonArray(new JsonObject { ["id"] = segId, ["from"] = oldT, ["to"] = oldT, ["count"] = 1 }),
            ["updatedAt"] = oldT,
        });
        return (segId, mediaId, mediaPath, oldT);
    }

    private JsonArray PageMessages() => _history.Page(_chatId, 0, 500).Messages;

    [Fact]
    public void ConfigStore_Defaults_KeepDays_Zero_Means_Never()
    {
        var cfg = _config.Current;
        Assert.Equal(0, (int?)cfg["history"]?["keepDays"]);
    }

    [Fact]
    public void ConfigStore_Normalize_Accepts_Zero()
    {
        var saved = _config.Save(new JsonObject { ["history"] = new JsonObject { ["keepDays"] = 0 } });
        Assert.Equal(0, (int?)saved["history"]?["keepDays"]);
    }

    [Fact]
    public void ConfigStore_Normalize_Accepts_UpTo365()
    {
        var saved = _config.Save(new JsonObject { ["history"] = new JsonObject { ["keepDays"] = 365 } });
        Assert.Equal(365, (int?)saved["history"]?["keepDays"]);
    }

    [Fact]
    public void Sweep_WithKeepDaysZero_DeletesNothing()
    {
        var (segId, mediaId, mediaPath, _) = SeedOldSegment();
        _config.Save(new JsonObject { ["history"] = new JsonObject { ["keepDays"] = 0 } });

        var r = _history.Sweep(_chatId);

        Assert.Equal(0, (int?)r["dropped"]);
        Assert.Equal(0, (int?)r["orphans"]);
        // 消息还在
        var msgs = PageMessages();
        Assert.Single(msgs);
        Assert.Equal("m-old-1", (string?)msgs[0]!["id"]);
        // 分片文件还在
        Assert.True(File.Exists(Path.Combine(_store.DataDir, "chats", _chatId, segId + ".json")), "分片文件不应被删");
        // 媒体还在
        Assert.True(File.Exists(mediaPath), $"keepDays=0 时媒体不应被删：{mediaPath}");
    }

    [Fact]
    public void Sweep_WithKeepDays3_KeepsMessages_DeletesOnlyOldMedia()
    {
        var (segId, mediaId, mediaPath, _) = SeedOldSegment();
        _config.Save(new JsonObject { ["history"] = new JsonObject { ["keepDays"] = 3 } });

        var r = _history.Sweep(_chatId);

        // 红线：文本永不删
        var msgs = PageMessages();
        Assert.Single(msgs);
        Assert.Equal("m-old-1", (string?)msgs[0]!["id"]);
        Assert.True(File.Exists(Path.Combine(_store.DataDir, "chats", _chatId, segId + ".json")), "分片文件不应被删");
        Assert.Equal(1, (int?)r["dropped"]); // 只删了媒体
        Assert.False(File.Exists(mediaPath), "超过保留期的媒体应被删");
    }

    [Fact]
    public void Sweep_RecentMedia_Survives()
    {
        var (_, _, mediaPath, _) = SeedOldSegment();
        // 把媒体改成"刚写的"，keepDays=3 时不能被 cutoff 清掉
        File.SetLastWriteTimeUtc(mediaPath, DateTime.UtcNow);
        _config.Save(new JsonObject { ["history"] = new JsonObject { ["keepDays"] = 3 } });

        _history.Sweep(_chatId);

        Assert.True(File.Exists(mediaPath), "新写入的媒体不应被删");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
