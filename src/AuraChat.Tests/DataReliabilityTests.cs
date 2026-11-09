using System.Text.Json.Nodes;
using AuraChat.Core;
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

/// <summary>
/// 数据可靠性回归（合同 t2 数据部分）：
/// 读的四种状态可区分（缺失/正常/损坏/读不了），坏文件绝不静默被默认值覆盖；
/// 写走唯一临时文件 + 冲盘 + 原子覆盖，同进程并发与残留 tmp 都安全；
/// 历史分片：孤儿收编、损坏缺口显式可见、原文件永不自动删除。
/// </summary>
public class DataReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aurachat-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录删不掉就算了 */ }
    }

    private (DataStore Store, AppLog Log) NewStore()
    {
        var store = new DataStore(_root);
        return (store, new AppLog());
    }

    // ---------- DataStore：读状态区分 ----------

    [Fact]
    public void ReadJsonDetailed_DistinguishesMissingOkCorrupted()
    {
        var (store, _) = NewStore();
        var file = store.PathIn("t.json");

        Assert.Equal(JsonReadStatus.Missing, DataStore.ReadJsonDetailed(file).Status);

        DataStore.WriteJson(file, new JsonObject { ["a"] = 1 });
        var ok = DataStore.ReadJsonDetailed(file);
        Assert.Equal(JsonReadStatus.Ok, ok.Status);
        Assert.Equal(1, ok.Node?["a"]?.GetValue<int>());

        File.WriteAllText(file, "{\"a\": 1");   // 半截 JSON（模拟写中断后的损坏）
        var bad = DataStore.ReadJsonDetailed(file);
        Assert.Equal(JsonReadStatus.Corrupted, bad.Status);
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public void ReadJsonDetailed_IoError_WhenFileIsLocked()
    {
        var (store, _) = NewStore();
        var file = store.PathIn("locked.json");
        File.WriteAllText(file, "{}");
        // 独占打开（不许共享读）模拟文件被别的进程锁住
        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = DataStore.ReadJsonDetailed(file);
        Assert.Equal(JsonReadStatus.IoError, r.Status);
        Assert.NotNull(r.Error);
    }

    // ---------- DataStore：写原子性 / 并发 / 残留 tmp ----------

    [Fact]
    public async Task WriteJson_ConcurrentWriters_SameTarget_AlwaysLeavesValidFile()
    {
        var (store, _) = NewStore();
        var file = store.PathIn("concurrent.json");
        DataStore.WriteJson(file, new JsonObject { ["n"] = 0 });

        const int writers = 8, perWriter = 25;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async w =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await Task.Delay(Random.Shared.Next(0, 3));
                DataStore.WriteJson(file, new JsonObject { ["writer"] = w, ["seq"] = i });
            }
        }).ToArray());

        var r = DataStore.ReadJsonDetailed(file);
        Assert.Equal(JsonReadStatus.Ok, r.Status);   // 任何时刻都该是某一个写者的完整内容
        Assert.True((r.Node?["seq"]?.GetValue<int>() ?? -1) >= 0);
        // 没有残留 tmp（写失败会自清，成功会被 Move 走）
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp-*"));
    }

    [Fact]
    public void WriteJson_ConcurrentWriters_DifferentTargets_NoCrossTalk()
    {
        var (store, _) = NewStore();
        Parallel.For(0, 12, w =>
        {
            for (var i = 0; i < 10; i++)
                DataStore.WriteJson(store.PathIn($"w{w}.json"), new JsonObject { ["w"] = w, ["i"] = i });
        });
        for (var w = 0; w < 12; w++)
        {
            var r = DataStore.ReadJsonDetailed(store.PathIn($"w{w}.json"));
            Assert.Equal(JsonReadStatus.Ok, r.Status);
            Assert.Equal(w, r.Node?["w"]?.GetValue<int>());
        }
    }

    [Fact]
    public void ReadJson_IgnoresLeftoverTmpFiles()
    {
        var (store, _) = NewStore();
        var file = store.PathIn("main.json");
        DataStore.WriteJson(file, new JsonObject { ["ok"] = true });
        File.WriteAllText(file + ".tmp-123-deadbeef", "{\"half");   // 模拟崩溃残留
        var r = DataStore.ReadJsonDetailed(file);
        Assert.Equal(JsonReadStatus.Ok, r.Status);
        Assert.True(DataStore.IsTempFile(file + ".tmp-123-deadbeef"));
        Assert.False(DataStore.IsTempFile(file));
    }

    // ---------- ConfigStore：损坏可见，不静默覆盖 ----------

    [Fact]
    public void ConfigLoad_CorruptedFile_BackupAndDefaults_NeverOverwriteOriginal()
    {
        var (store, log) = NewStore();
        var file = store.PathIn("config.json");
        const string broken = "{\"web\": { \"host\": \"192.168.1.5\", ";   // 有价值的半截配置
        File.WriteAllText(file, broken);

        var cfg = new ConfigStore(store, log).Load();
        Assert.NotNull(cfg.LastLoadIssue);                                   // 显式诊断
        Assert.Equal("127.0.0.1", (string?)cfg.Current["web"]?["host"]);     // 本轮用默认值起来

        // 原文件被原样备份（内容一字节不丢），而不是被默认配置覆盖
        var backups = Directory.GetFiles(Path.GetDirectoryName(file)!, "config.json.corrupt-*");
        Assert.Single(backups);
        Assert.Equal(broken, File.ReadAllText(backups[0]));
        Assert.False(File.Exists(file));   // 坏文件已挪走（Move 备份），不在原地等覆盖
    }

    [Fact]
    public void ConfigLoad_IoError_KeepsFileUntouched()
    {
        var (store, log) = NewStore();
        var file = store.PathIn("config.json");
        File.WriteAllText(file, "{\"web\": {\"host\": \"192.168.9.9\"}}");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var cfg = new ConfigStore(store, log).Load();
            Assert.NotNull(cfg.LastLoadIssue);
            Assert.Equal("127.0.0.1", (string?)cfg.Current["web"]?["host"]);   // 内存里用默认值
        }
        Assert.Equal("{\"web\": {\"host\": \"192.168.9.9\"}}", File.ReadAllText(file));   // 原文件一字节没动
    }

    [Fact]
    public void ConfigLoad_MissingFile_IsNotAnIssue()
    {
        var (store, log) = NewStore();
        var cfg = new ConfigStore(store, log).Load();
        Assert.Null(cfg.LastLoadIssue);
        Assert.Equal("127.0.0.1", (string?)cfg.Current["web"]?["host"]);
    }

    // ---------- AuthService：账号文件损坏不静默重建 ----------

    [Fact]
    public void UsersFile_Corrupted_NoSilentTakeover()
    {
        var (store, log) = NewStore();
        var file = store.PathIn("users.json");
        File.WriteAllText(file, "{\"owner\": {\"username\": \"boss\"");

        var auth = new AuthService(store, log);
        Assert.False(auth.NeedSetup);   // 不装作「还没建主人」——那会引导用户重建并把坏文件覆盖掉
        var setup = auth.SetupOwner("attacker", "password123");
        Assert.False(setup.Ok);         // 损坏状态下拒绝建新主人
        var login = auth.Verify("attacker", "password123");
        Assert.False(login.Ok);
        Assert.Equal("{\"owner\": {\"username\": \"boss\"", File.ReadAllText(file));   // 原文件未被覆盖
    }

    [Fact]
    public void UsersFile_CorruptedAfterGoodState_NextLoadSurfacesIssue()
    {
        var (store, log) = NewStore();
        var auth = new AuthService(store, log);
        Assert.True(auth.SetupOwner("boss", "password123").Ok);
        File.WriteAllText(store.PathIn("users.json"), "not-json-at-all");

        Assert.False(auth.NeedSetup);
        Assert.NotNull(auth.AccountFileIssue);
        Assert.False(auth.Verify("boss", "password123").Ok);
    }

    // ---------- HistoryStore：孤儿分片 / 缺口可见 / 原文件不删 ----------

    private sealed record Hist(DataStore Store, FeatureHost Host, HistoryStore History, MediaStore Media, AppLog Log);

    private Hist NewHistory()
    {
        var store = new DataStore(Path.Combine(_root, "h" + Guid.NewGuid().ToString("N")[..6]));
        var log = new AppLog();
        var config = new ConfigStore(store, log).Load();
        var auth = new AuthService(store, log);
        var persona = new PersonaService(store, Path.Combine(store.Root, "personas-bundled"));
        var host = new FeatureHost { Store = store, Config = config, Auth = auth, Log = log, Persona = persona };
        var media = new MediaStore(store, log);
        return new Hist(store, host, new HistoryStore(host, media), media, log);
    }

    private static string ChatDirOf(Hist h, string chatId)
        => Path.Combine(h.Store.DataDir, "chats", HistoryStore.SafeName(chatId));

    [Fact]
    public void OrphanSegment_IsMergedBackIntoIndex()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "orphan-chat";

        // 正常写两条
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "第一条", ["t"] = 1000 });
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "第二条", ["t"] = 2000 });

        // 模拟「分片写成功但索引更新失败」的崩溃现场：直接落一个索引不认识的分片文件
        var segId = $"seg-009-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-orphan1";
        var segPath = Path.Combine(ChatDirOf(h, chatId), segId + ".json");
        File.WriteAllText(segPath, new JsonObject
        {
            ["chatId"] = chatId,
            ["id"] = segId,
            ["from"] = 500,
            ["to"] = 600,
            ["messages"] = new JsonArray(new JsonObject { ["who"] = "user", ["text"] = "孤儿片里的消息", ["t"] = 500 }),
        }.ToJsonString());

        var page = history.Page(chatId, 0, 50);
        Assert.Contains(page.Messages, m => (string?)m?["text"] == "孤儿片里的消息");   // 收编后翻页可见
        Assert.Equal(3, page.Messages.Count);

        var health = history.Health(chatId);
        Assert.Empty((JsonArray)health["orphanFiles"]!);   // 对账后不再有孤儿
        Assert.Equal("Ok", (string?)health["indexStatus"]);
    }

    [Fact]
    public void CorruptedSegment_GapVisible_OriginalKept_OthersIntact()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "corrupt-chat";
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "好的消息", ["t"] = 1000 });
        var segFile = Directory.GetFiles(ChatDirOf(h, chatId), "seg-*.json").Single();

        // 旁边再放一个损坏分片并登记进索引（模拟磁盘位翻转/写残）
        var badId = "seg-002-badc0de-bad";
        File.WriteAllText(Path.Combine(ChatDirOf(h, chatId), badId + ".json"), "{\"messages\": [{\"t\"");
        var idxPath = Path.Combine(ChatDirOf(h, chatId), "index.json");
        var idx = JsonNode.Parse(File.ReadAllText(idxPath))!.AsObject();
        ((JsonArray)idx["segments"]!).Add(new JsonObject { ["id"] = badId, ["from"] = 1, ["to"] = 2, ["count"] = 1 });
        File.WriteAllText(idxPath, idx.ToJsonString());

        var page = history.Page(chatId, 0, 50);
        Assert.Single(page.Messages);                              // 好分片照常翻出来
        Assert.Equal("好的消息", (string?)page.Messages[0]?["text"]);

        var health = history.Health(chatId);
        var unreadable = (JsonArray)health["unreadableSegments"]!;
        Assert.Single(unreadable);                                 // 缺口显式可见
        Assert.Equal(badId, (string?)unreadable[0]?["id"]);

        Assert.True(File.Exists(segFile));                         // 原文件一个都没删
        Assert.True(File.Exists(Path.Combine(ChatDirOf(h, chatId), badId + ".json")));
    }

    [Fact]
    public void Append_WhenCurrentSegmentUnreadable_RollsNewSegment_NeverOverwrites()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "roll-chat";
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "旧消息", ["t"] = 1000 });
        var dir = ChatDirOf(h, chatId);
        var segFile = Directory.GetFiles(dir, "seg-*.json").Single();
        File.WriteAllText(segFile, "{\"messages\": [");   // 当前片损坏

        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "新消息", ["t"] = 2000 });

        Assert.Equal("{\"messages\": [", File.ReadAllText(segFile));   // 坏片字节原地保留，绝没有被覆盖
        var page = history.Page(chatId, 0, 50);
        Assert.Contains(page.Messages, m => (string?)m?["text"] == "新消息");
        var health = history.Health(chatId);
        Assert.Single((JsonArray)health["unreadableSegments"]!);       // 缺口可见
    }

    [Fact]
    public void CorruptedIndex_IsRebuiltFromShards_OriginalDataIntact()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "reindex-chat";
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "一", ["t"] = 1000 });
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "二", ["t"] = 2000 });
        var dir = ChatDirOf(h, chatId);
        var segFile = Directory.GetFiles(dir, "seg-*.json").Single();
        File.WriteAllText(Path.Combine(dir, "index.json"), "{{{broken");

        var page = history.Page(chatId, 0, 50);
        Assert.Equal(2, page.Messages.Count);
        Assert.True(File.Exists(segFile));
        Assert.Equal("Ok", (string?)history.Health(chatId)["indexStatus"]);
    }

    [Fact]
    public void MissingIndex_OrphanShardRebuild_DataSurvives()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "noidx-chat";
        history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "活下来的消息", ["t"] = 1000 });
        var dir = ChatDirOf(h, chatId);
        File.Delete(Path.Combine(dir, "index.json"));   // 索引整个丢了

        var page = history.Page(chatId, 0, 50);
        Assert.Single(page.Messages);
        Assert.Equal("活下来的消息", (string?)page.Messages[0]?["text"]);
        Assert.True(File.Exists(Path.Combine(dir, "index.json")));   // 重建后索引回来了
    }

    // ---------- 裁切残留不重复收编（坏片对账 → 健康计数） ----------

    [Fact]
    public void DuplicateResidueOrphan_NotReadopted_PageStaysUnique()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "dup-orphan-chat";
        var saved = history.Append(chatId, new JsonObject { ["who"] = "user", ["text"] = "唯一消息", ["t"] = 1000 });

        // 模拟裁切/崩溃残留：同一消息（同 id）再落一个索引外的片
        var segId = $"seg-009-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-dupresidue";
        var segPath = Path.Combine(ChatDirOf(h, chatId), segId + ".json");
        File.WriteAllText(segPath, new JsonObject
        {
            ["chatId"] = chatId,
            ["id"] = segId,
            ["from"] = 1000,
            ["to"] = 1000,
            ["messages"] = new JsonArray(saved.DeepClone()),
        }.ToJsonString());

        var page = history.Page(chatId, 0, 50);
        Assert.Equal(1, page.Messages.Count);          // 内容在索引里有可读副本：不收编，翻页不重复
        Assert.True(File.Exists(segPath));             // 残留文件原地保留，绝不自动删

        var health = history.Health(chatId);
        Assert.Contains(segId, ((JsonArray)health["orphanFiles"]!).Select(n => (string?)n));   // 残留显式可见
        Assert.Equal(1, health["messages"]!.GetValue<int>());            // 健康计数不因残留虚高
        Assert.Equal(1, health["readableMessages"]!.GetValue<int>());
    }

    [Fact]
    public void SplitResidue_UndeletableOldSegment_DoesNotDuplicateMessages()
    {
        var h = NewHistory();
        var history = h.History;
        var chatId = "split-residue-chat";
        var dir = ChatDirOf(h, chatId);
        Directory.CreateDirectory(dir);

        // 手工落一个 250 条的「当前片」（旧版数据/对账收编后的形状）
        const string oldId = "seg-001-legacy-old";
        var msgs = new JsonArray();
        for (var i = 0; i < 250; i++)
            msgs.Add(new JsonObject { ["id"] = $"m-{i:000}", ["who"] = "user", ["text"] = $"第{i}条", ["t"] = 1000 + i });
        var oldPath = Path.Combine(dir, oldId + ".json");
        File.WriteAllText(oldPath, new JsonObject
        {
            ["chatId"] = chatId,
            ["id"] = oldId,
            ["from"] = 1000,
            ["to"] = 1249,
            ["messages"] = msgs,
        }.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "index.json"), new JsonObject
        {
            ["chatId"] = chatId,
            ["segments"] = new JsonArray(new JsonObject { ["id"] = oldId, ["from"] = 1000, ["to"] = 1249, ["count"] = 250 }),
            ["updatedAt"] = 0L,
        }.ToJsonString());

        // 锁住旧片（允许读、不许删）＝ 杀软/备份占着文件：空闲裁切切完新片后删不掉旧片
        using (var hold = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            history.Finalize(chatId);
        }
        Assert.True(File.Exists(oldPath));   // 旧片字节原地保留

        // 关键回归：残留旧片不收编，翻页仍是 250 条、无重复（修复前会翻出 500 条）
        var page = history.Page(chatId, 0, 500);
        Assert.Equal(250, page.Messages.Count);
        Assert.Equal(250, page.Messages.Select(m => (string?)m?["id"]).Distinct().Count());

        var health = history.Health(chatId);
        Assert.Contains(oldId, ((JsonArray)health["orphanFiles"]!).Select(n => (string?)n));   // 残留可见
        Assert.Equal(250, health["messages"]!.GetValue<int>());          // 计数不虚高
        Assert.Equal(250, health["readableMessages"]!.GetValue<int>());
        Assert.Empty((JsonArray)health["unreadableSegments"]!);
        Assert.Equal("Ok", (string?)health["indexStatus"]);
    }

}
