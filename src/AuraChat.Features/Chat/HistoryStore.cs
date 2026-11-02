using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Media;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Chat;

/// <summary>
/// 聊天记录分片存储，逐条对齐原 chat/history.js：
///   消息按 100 条一片落在 chats/&lt;chatId&gt;/&lt;segId&gt;.json，索引在 index.json；
///   图片/语音的二进制走 MediaStore（media/&lt;chatId&gt;/&lt;mediaId&gt;），消息里只留 mediaId。
/// 写盘永远只碰「当前片」，所以 append 是常数级；翻页按分片倒着走。
/// </summary>
public sealed class HistoryStore : IHistoryStore
{
    /// <summary>一片最多多少条（原 SEGMENT_LIMIT）。</summary>
    public const int SegmentLimit = 100;

    /// <summary>空闲多久做一次裁切/清理（原 IDLE_MS = 2 分钟）。</summary>
    public const int IdleMs = 2 * 60 * 1000;

    /// <summary>默认保留天数（原 DEFAULT_KEEP_DAYS）。</summary>
    public const int DefaultKeepDays = 7;

    /// <summary>孤儿媒体至少放 1 小时再删（原 ORPHAN_GRACE_MS）。</summary>
    public const long OrphanGraceMs = 60 * 60 * 1000;

    private static readonly System.Text.RegularExpressions.Regex SeqRe = new(@"^seg-\d{3,}-", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly DataStore _store;
    private readonly MediaStore _media;
    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _typing = new(StringComparer.Ordinal);

    public HistoryStore(FeatureHost host, MediaStore? media = null)
    {
        _host = host;
        _store = host.Store;
        _log = host.Log.Child("history");
        _media = media ?? host.Get<MediaStore>() ?? new MediaStore(host.Store, host.Log);
        host.Set<IHistoryStore>(this);
    }

    // ---------- 路径（原 history.js:41-57） ----------

    /// <summary>会话 id → 目录名（只留安全字符，截 80，空则 x）。</summary>
    public static string SafeName(string? name)
    {
        var s = new string((name ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        if (s.Length > 80) s = s[..80];
        return s.Length == 0 ? "x" : s;
    }

    private string ChatsRoot() => Path.Combine(_store.DataDir, "chats");

    private string ChatDir(string chatId) => Path.Combine(ChatsRoot(), SafeName(chatId));

    private string IndexFile(string chatId) => Path.Combine(ChatDir(chatId), "index.json");

    private string SegFile(string chatId, string id) => Path.Combine(ChatDir(chatId), SafeName(id) + ".json");

    private string ReadFile(string chatId) => Path.Combine(ChatDir(chatId), "read.json");

    // ---------- 小工具 ----------

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long Long(JsonNode? node, long fallback = 0)
    {
        if (node is null) return fallback;
        try { return node.GetValue<long>(); } catch { /* 不是 long 再试 */ }
        try { return (long)node.GetValue<double>(); } catch { /* 再试字符串 */ }
        return long.TryParse(node.ToString(), out var n) ? n : fallback;
    }

    private static int Int(JsonNode? node, int fallback = 0) => (int)Long(node, fallback);

    private static string Text(JsonNode? node, string fallback = "")
    {
        if (node is null) return fallback;
        try { return node.GetValue<string>(); } catch { return node.ToString(); }
    }

    private static bool Flag(JsonNode? node) => JsonUtil.Bool(node, false);

    private static JsonArray CloneArray(JsonArray? src)
    {
        var arr = new JsonArray();
        if (src is null) return arr;
        foreach (var node in src) arr.Add(node?.DeepClone());
        return arr;
    }

    private static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value <= 0) return "0";
        var buf = new char[16];
        var i = buf.Length;
        while (value > 0 && i > 0)
        {
            buf[--i] = digits[(int)(value % 36)];
            value /= 36;
        }
        return new string(buf, i, buf.Length - i);
    }

    private int KeepDays()
    {
        var n = _host.Num(DefaultKeepDays, "history", "keepDays");
        return n is > 0 and <= 365 ? (int)n : DefaultKeepDays;
    }

    // ---------- 索引（原 readIndex / ensureSeq / rebuildIndex / writeIndex） ----------

    private JsonObject ReadIndexLocked(string chatId)
    {
        var idx = DataStore.ReadJson(IndexFile(chatId)) as JsonObject;
        if (idx is not null && idx["segments"] is JsonArray) return EnsureSeqLocked(chatId, idx);
        return EnsureSeqLocked(chatId, RebuildIndexLocked(chatId));
    }

    /// <summary>老分片 id 补上序号（seg-001-…）；目标已存在就跳过。</summary>
    private JsonObject EnsureSeqLocked(string chatId, JsonObject idx)
    {
        if (idx["segments"] is not JsonArray segs) return idx;
        var dirty = false;
        for (var i = 0; i < segs.Count; i++)
        {
            if (segs[i] is not JsonObject seg) continue;
            var id = Text(seg["id"]);
            if (id.Length == 0 || SeqRe.IsMatch(id)) continue;
            var next = $"seg-{i + 1:000}-" + id.Replace("seg-", "", StringComparison.Ordinal);
            if (next == id) continue;
            try
            {
                if (File.Exists(SegFile(chatId, next))) continue;
                File.Move(SegFile(chatId, id), SegFile(chatId, next));
            }
            catch
            {
                continue;
            }
            seg["id"] = next;
            dirty = true;
        }
        if (dirty) WriteIndexLocked(chatId, idx);
        return idx;
    }

    /// <summary>index.json 丢了就扫目录重建（顺手修掉被手动删过片的索引）。</summary>
    private JsonObject RebuildIndexLocked(string chatId)
    {
        var idx = new JsonObject { ["chatId"] = chatId, ["segments"] = new JsonArray(), ["updatedAt"] = 0 };
        string[] files;
        try
        {
            files = Directory.Exists(ChatDir(chatId))
                ? Directory.GetFiles(ChatDir(chatId), "*.json")
                : [];
        }
        catch
        {
            return idx;
        }

        var found = new List<(long From, JsonObject Seg)>();
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file), "index.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (DataStore.ReadObject(file, new JsonObject()) is not { } data) continue;
            if (data["messages"] is not JsonArray messages || messages.Count == 0) continue;
            var from = Long(data["from"], Long(messages[0]?["t"]));
            var to = Long(data["to"], Long(messages[messages.Count - 1]?["t"]));
            var id = Text(data["id"], Path.GetFileNameWithoutExtension(file));
            found.Add((from, new JsonObject { ["id"] = id, ["from"] = from, ["to"] = to, ["count"] = messages.Count }));
        }
        found.Sort((a, b) => a.From.CompareTo(b.From));
        var segs = new JsonArray();
        foreach (var (_, seg) in found) segs.Add(seg);
        idx["segments"] = segs;
        idx["updatedAt"] = Now;
        if (segs.Count > 0) DataStore.WriteJson(IndexFile(chatId), idx);
        return idx;
    }

    private void WriteIndexLocked(string chatId, JsonObject idx)
    {
        idx["chatId"] = chatId;
        idx["updatedAt"] = Now;
        DataStore.WriteJson(IndexFile(chatId), idx);
    }

    private JsonObject? ReadSegLocked(string chatId, string id)
    {
        var data = DataStore.ReadJson(SegFile(chatId, id)) as JsonObject;
        return data?["messages"] is JsonArray ? data : null;
    }

    private static string NewSegmentId(int seq)
        => $"seg-{seq:000}-{ToBase36(Now)}-{Guid.NewGuid().ToString("N")[..6]}";

    // ---------- 写：追加 / 改最后一条 ----------

    public JsonObject Append(string chatId, JsonObject message)
    {
        _io.Wait();
        try
        {
            var msg = (JsonObject)message.DeepClone();
            if (msg["id"] is null) msg["id"] = Guid.NewGuid().ToString("D");
            if (msg["t"] is null) msg["t"] = Now;
            else msg["t"] = Long(msg["t"]);
            ExtractMediaLocked(chatId, msg);

            var idx = ReadIndexLocked(chatId);
            var segs = (JsonArray)idx["segments"]!;
            JsonObject? seg = segs.Count > 0 ? segs[segs.Count - 1] as JsonObject : null;

            // 当前片写满就滚动：单文件永远 ≤ SegmentLimit 条
            if (seg is null || Int(seg["count"]) >= SegmentLimit)
            {
                seg = new JsonObject
                {
                    ["id"] = NewSegmentId(segs.Count + 1),
                    ["from"] = Long(msg["t"]),
                    ["to"] = Long(msg["t"]),
                    ["count"] = 0,
                };
                segs.Add(seg);
                WriteIndexLocked(chatId, idx);
            }

            var data = ReadSegLocked(chatId, Text(seg["id"]))
                       ?? new JsonObject { ["chatId"] = chatId, ["id"] = Text(seg["id"]), ["from"] = Long(msg["t"]), ["to"] = Long(msg["t"]), ["messages"] = new JsonArray() };
            var messages = (JsonArray)data["messages"]!;
            messages.Add(msg);
            data["from"] = Long(messages[0]?["t"]);
            data["to"] = Long(msg["t"]);
            DataStore.WriteJson(SegFile(chatId, Text(seg["id"])), data);

            seg["from"] = data["from"]!.DeepClone();
            seg["to"] = data["to"]!.DeepClone();
            seg["count"] = messages.Count;
            WriteIndexLocked(chatId, idx);
            return msg;
        }
        finally
        {
            _io.Release();
        }
    }

    public JsonObject PatchLast(string chatId, JsonObject patch)
    {
        _io.Wait();
        try
        {
            var idx = ReadIndexLocked(chatId);
            var segs = (JsonArray)idx["segments"]!;
            if (segs.Count == 0 || segs[segs.Count - 1] is not JsonObject seg) return new JsonObject();
            var data = ReadSegLocked(chatId, Text(seg["id"]));
            if (data?["messages"] is not JsonArray messages || messages.Count == 0) return new JsonObject();
            if (messages[messages.Count - 1] is not JsonObject last) return new JsonObject();
            foreach (var (key, value) in patch) last[key] = value?.DeepClone();
            DataStore.WriteJson(SegFile(chatId, Text(seg["id"])), data);
            return last;
        }
        finally
        {
            _io.Release();
        }
    }

    // ---------- 读：翻页 ----------

    public (JsonArray Messages, long OldestAt, bool HasMore) Page(string chatId, long before, int limit)
    {
        _io.Wait();
        try
        {
            return PageLocked(chatId, before, limit);
        }
        finally
        {
            _io.Release();
        }
    }

    private (JsonArray Messages, long OldestAt, bool HasMore) PageLocked(string chatId, long before, int limit)
    {
        var take = Math.Clamp(limit <= 0 ? SegmentLimit : limit, 1, 500);
        var idx = ReadIndexLocked(chatId);
        var segs = (JsonArray)idx["segments"]!;
        var outArr = new JsonArray();

        for (var s = segs.Count - 1; s >= 0; s--)
        {
            if (segs[s] is not JsonObject seg || Int(seg["count"]) <= 0) continue;
            var data = ReadSegLocked(chatId, Text(seg["id"]));
            if (data?["messages"] is not JsonArray messages) continue;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m is null) continue;
                // 同一毫秒的多条会卡在边界：边界这一条重复返回，由客户端按 id 去重
                if (before > 0 && Long(m["t"]) > before) continue;
                outArr.Insert(0, m.DeepClone());
                if (outArr.Count >= take)
                {
                    var moreInSeg = i > 0;
                    var moreEarlier = moreInSeg || s > 0 ? HasAnyLocked(chatId, segs, s, moreInSeg, Long(m["t"])) : false;
                    return (outArr, Long(outArr[0]?["t"]), moreEarlier);
                }
            }
        }
        return (outArr, outArr.Count > 0 ? Long(outArr[0]?["t"]) : 0, false);
    }

    public (JsonArray Messages, long OldestAt, bool HasMore, int Index, int Total, string? FileId) PageFile(string chatId, string file, long before)
    {
        _io.Wait();
        try
        {
            return PageFileLocked(chatId, file, before);
        }
        finally
        {
            _io.Release();
        }
    }

    private (JsonArray Messages, long OldestAt, bool HasMore, int Index, int Total, string? FileId) PageFileLocked(string chatId, string file, long before)
    {
        // file=last 取最新一片；file=prev 取 to < before 的最后一片
        var cursor = string.Equals(file, "prev", StringComparison.OrdinalIgnoreCase) ? before : 0;
        var idx = ReadIndexLocked(chatId);
        var segs = new List<JsonObject>();
        foreach (var node in (JsonArray)idx["segments"]!)
        {
            if (node is JsonObject item && Int(item["count"]) > 0) segs.Add(item);
        }
        if (segs.Count == 0) return (new JsonArray(), 0, false, -1, 0, null);

        var pos = segs.Count - 1;
        if (cursor > 0)
        {
            pos = -1;
            for (var i = segs.Count - 1; i >= 0; i--)
            {
                if (Long(segs[i]["to"]) < cursor)
                {
                    pos = i;
                    break;
                }
            }
            if (pos < 0) return (new JsonArray(), 0, false, -1, segs.Count, null);
        }

        var seg = segs[pos];
        var data = ReadSegLocked(chatId, Text(seg["id"]));
        var messages = CloneArray(data?["messages"] as JsonArray);
        return (messages, messages.Count > 0 ? Long(messages[0]?["t"]) : 0, pos > 0, pos, segs.Count, Text(seg["id"]));
    }

    /// <summary>还有没有比 t 更早的消息：同片前面有，或更旧的片里有。</summary>
    private bool HasAnyLocked(string chatId, JsonArray segs, int s, bool moreInSeg, long t)
    {
        if (moreInSeg) return true;
        for (var i = s - 1; i >= 0; i--)
        {
            if (segs[i] is not JsonObject seg || Int(seg["count"]) <= 0) continue;
            var data = ReadSegLocked(chatId, Text(seg["id"]));
            if (data?["messages"] is JsonArray messages && messages.Any(m => m is not null && Long(m["t"]) < t)) return true;
        }
        return false;
    }

    public JsonArray Recent(string chatId, int count)
        => Page(chatId, 0, Math.Max(1, count)).Messages;

    public JsonObject? Last(string chatId)
    {
        _io.Wait();
        try
        {
            var idx = ReadIndexLocked(chatId);
            var segs = (JsonArray)idx["segments"]!;
            if (segs.Count == 0 || segs[segs.Count - 1] is not JsonObject seg) return null;
            var data = ReadSegLocked(chatId, Text(seg["id"]));
            return data?["messages"] is JsonArray { Count: > 0 } messages ? messages[messages.Count - 1] as JsonObject : null;
        }
        finally
        {
            _io.Release();
        }
    }

    public long OldestAt(string chatId)
    {
        _io.Wait();
        try
        {
            var idx = ReadIndexLocked(chatId);
            var segs = (JsonArray)idx["segments"]!;
            if (segs.Count == 0 || segs[0] is not JsonObject seg) return 0;
            var data = ReadSegLocked(chatId, Text(seg["id"]));
            return data?["messages"] is JsonArray { Count: > 0 } messages ? Long(messages[0]?["t"]) : 0;
        }
        finally
        {
            _io.Release();
        }
    }

    public int TotalOf(string chatId)
    {
        _io.Wait();
        try
        {
            var idx = ReadIndexLocked(chatId);
            var total = 0;
            foreach (var node in (JsonArray)idx["segments"]!)
            {
                if (node is JsonObject seg) total += Int(seg["count"]);
            }
            return total;
        }
        finally
        {
            _io.Release();
        }
    }

    // ---------- 过期清理 ----------

    public JsonObject Sweep(string chatId)
    {
        _io.Wait();
        try
        {
            return SweepLocked(chatId);
        }
        finally
        {
            _io.Release();
        }
    }

    private JsonObject SweepLocked(string chatId)
    {
        var cutoff = Now - KeepDays() * 86_400_000L;
        var idx = ReadIndexLocked(chatId);
        var segs = (JsonArray)idx["segments"]!;
        var keep = new JsonArray();
        var dropped = 0;
        var trimmed = false;

        foreach (var node in segs.ToList())
        {
            if (node is not JsonObject seg || Int(seg["count"]) <= 0) continue;
            var id = Text(seg["id"]);

            // 整片过期：连片带媒体一起删
            if (Long(seg["to"]) <= cutoff)
            {
                if (ReadSegLocked(chatId, id)?["messages"] is JsonArray old)
                {
                    foreach (var m in old)
                    {
                        if (m?["mediaId"] is { } mediaId) _media.Delete(chatId, Text(mediaId));
                        dropped++;
                    }
                }
                TryDelete(SegFile(chatId, id));
                continue;
            }

            // 片跨过期线：掐掉过期的消息重写
            if (Long(seg["from"]) <= cutoff)
            {
                var data = ReadSegLocked(chatId, id);
                var rest = new JsonArray();
                if (data?["messages"] is JsonArray messages)
                {
                    foreach (var m in messages)
                    {
                        if (m is not null && Long(m["t"]) > cutoff)
                        {
                            rest.Add(m.DeepClone());
                            continue;
                        }
                        if (m?["mediaId"] is { } mediaId) _media.Delete(chatId, Text(mediaId));
                        dropped++;
                    }
                }
                if (rest.Count == 0)
                {
                    TryDelete(SegFile(chatId, id));
                    continue;
                }
                seg["from"] = Long(rest[0]?["t"]);
                seg["to"] = Long(rest[rest.Count - 1]?["t"]);
                seg["count"] = rest.Count;
                DataStore.WriteJson(SegFile(chatId, id), new JsonObject
                {
                    ["chatId"] = chatId,
                    ["id"] = id,
                    ["from"] = seg["from"]!.DeepClone(),
                    ["to"] = seg["to"]!.DeepClone(),
                    ["messages"] = rest,
                });
                trimmed = true;
                keep.Add(seg.DeepClone());
                continue;
            }

            keep.Add(seg.DeepClone());
        }

        if (keep.Count != segs.Count || trimmed)
        {
            idx["segments"] = keep;
            WriteIndexLocked(chatId, idx);
        }

        // media 目录里比保留期更旧的文件一律清掉（消息片删掉后的孤儿也在这里）
        try
        {
            var dir = _media.DirOf(chatId);
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    try
                    {
                        if (new FileInfo(f).LastWriteTimeUtc < DateTimeOffset.FromUnixTimeMilliseconds(cutoff).UtcDateTime)
                        {
                            File.Delete(f);
                        }
                    }
                    catch { /* 单个媒体删不掉不影响清理 */ }
                }
            }
        }
        catch { /* 目录读不了就跳过 */ }

        var orphans = ReapOrphanMediaLocked(chatId);
        var oldest = OldestAtLocked(chatId);
        var total = TotalOfLocked(chatId);
        return new JsonObject
        {
            ["changed"] = dropped > 0,
            ["dropped"] = dropped,
            ["orphans"] = orphans,
            ["oldestAt"] = oldest,
            ["hasMore"] = total > 0,
        };
    }

    /// <summary>孤儿媒体：没有任何消息引用、且落盘超过 grace 的文件删掉。</summary>
    private int ReapOrphanMediaLocked(string chatId, long graceMs = OrphanGraceMs)
    {
        var dir = _media.DirOf(chatId);
        if (!Directory.Exists(dir)) return 0;
        string[] files;
        try { files = Directory.GetFiles(dir); } catch { return 0; }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var idx = ReadIndexLocked(chatId);
        foreach (var node in (JsonArray)idx["segments"]!)
        {
            if (node is not JsonObject seg || Int(seg["count"]) <= 0) continue;
            var data = ReadSegLocked(chatId, Text(seg["id"]));
            if (data?["messages"] is not JsonArray messages) continue;
            foreach (var m in messages)
            {
                if (m?["mediaId"] is { } mediaId) used.Add(SafeName(Text(mediaId)));
            }
        }

        var now = Now;
        var removed = 0;
        foreach (var file in files)
        {
            if (used.Contains(SafeName(Path.GetFileName(file)))) continue;
            try
            {
                if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds() > graceMs)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch { /* 删不掉下次再试 */ }
        }
        return removed;
    }

    private long OldestAtLocked(string chatId)
    {
        var idx = ReadIndexLocked(chatId);
        var segs = (JsonArray)idx["segments"]!;
        if (segs.Count == 0 || segs[0] is not JsonObject seg) return 0;
        var data = ReadSegLocked(chatId, Text(seg["id"]));
        return data?["messages"] is JsonArray { Count: > 0 } messages ? Long(messages[0]?["t"]) : 0;
    }

    private int TotalOfLocked(string chatId)
    {
        var idx = ReadIndexLocked(chatId);
        var total = 0;
        foreach (var node in (JsonArray)idx["segments"]!)
        {
            if (node is JsonObject seg) total += Int(seg["count"]);
        }
        return total;
    }

    public List<JsonObject> SweepAll()
    {
        var outList = new List<JsonObject>();
        string[] dirs;
        try { dirs = Directory.Exists(ChatsRoot()) ? Directory.GetDirectories(ChatsRoot()) : []; }
        catch { return outList; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            var r = Sweep(name);
            if (Flag(r["changed"]))
            {
                outList.Add(new JsonObject
                {
                    ["chatId"] = name,
                    ["changed"] = true,
                    ["dropped"] = r["dropped"]?.DeepClone(),
                    ["orphans"] = r["orphans"]?.DeepClone(),
                    ["oldestAt"] = r["oldestAt"]?.DeepClone(),
                    ["hasMore"] = r["hasMore"]?.DeepClone(),
                });
            }
        }

        // 会话目录已经没了的媒体目录直接清掉
        try
        {
            var mediaRoot = Path.Combine(_store.Root, "media");
            if (Directory.Exists(mediaRoot))
            {
                foreach (var dir in Directory.GetDirectories(mediaRoot))
                {
                    if (!Directory.Exists(Path.Combine(ChatsRoot(), Path.GetFileName(dir))))
                    {
                        try { Directory.Delete(dir, true); } catch { /* 清不掉留着 */ }
                    }
                }
            }
        }
        catch { /* 清理失败不影响返回 */ }
        return outList;
    }

    public JsonObject Stats()
    {
        var result = new JsonObject { ["chats"] = 0, ["segments"] = 0, ["messages"] = 0, ["textBytes"] = 0L, ["mediaBytes"] = 0L };
        string[] dirs;
        try { dirs = Directory.Exists(ChatsRoot()) ? Directory.GetDirectories(ChatsRoot()) : []; }
        catch { return result; }
        long textBytes = 0, mediaBytes = 0;
        var segments = 0;
        var messages = 0;
        foreach (var dir in dirs)
        {
            try
            {
                var idx = DataStore.ReadObject(Path.Combine(dir, "index.json"));
                if (idx["segments"] is JsonArray segs)
                {
                    segments += segs.Count;
                    foreach (var node in segs)
                    {
                        if (node is not JsonObject seg) continue;
                        messages += Int(seg["count"]);
                        try { textBytes += new FileInfo(Path.Combine(dir, SafeName(Text(seg["id"])) + ".json")).Length; } catch { }
                    }
                }
                var mediaDir = _media.DirOf(Path.GetFileName(dir));
                if (Directory.Exists(mediaDir))
                {
                    foreach (var f in Directory.GetFiles(mediaDir)) mediaBytes += new FileInfo(f).Length;
                }
            }
            catch { /* 单个会话统计失败跳过 */ }
        }
        result["chats"] = dirs.Length;
        result["segments"] = segments;
        result["messages"] = messages;
        result["textBytes"] = textBytes;
        result["mediaBytes"] = mediaBytes;
        return result;
    }

    // ---------- 空闲裁切 ----------

    public void Touch(string chatId)
    {
        CancelTimer(chatId);
        var cts = new CancellationTokenSource();
        _timers[chatId] = cts;
        _ = Task.Delay(IdleMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try { Finalize(chatId); }
            catch (Exception ex) { _log.Warn("空闲裁切出错 {0}：{1}", chatId, ex.Message); }
        }, TaskScheduler.Default);
    }

    public void Finalize(string chatId)
    {
        _io.Wait();
        try
        {
            CancelTimer(chatId);
            List<int>? split = null;
            try { split = SplitCurrentLocked(chatId); }
            catch (Exception ex) { _log.Warn("空闲裁切出错 {0}：{1}", chatId, ex.Message); }

            var r = SweepLocked(chatId);
            if (split is not null) _log.Info("空闲裁切 {0}：{1}", chatId, string.Join("+", split));

            if (Flag(r["changed"]))
            {
                _log.Info("聊天记录过期清理 {0}：删了 {1} 条", chatId, Int(r["dropped"]));
                var total = TotalOfLocked(chatId);
                _host.Emit("history", new JsonObject
                {
                    ["chatId"] = chatId,
                    ["oldestAt"] = Long(r["oldestAt"]),
                    ["droppedBefore"] = Long(r["oldestAt"]),
                    ["hasMore"] = total > SegmentLimit,
                });
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>把当前片里超过 100 条的部分切成每片 100，最后一片当新的当前片。</summary>
    private List<int>? SplitCurrentLocked(string chatId)
    {
        var idx = ReadIndexLocked(chatId);
        var segs = (JsonArray)idx["segments"]!;
        if (segs.Count == 0 || segs[segs.Count - 1] is not JsonObject last) return null;
        if (Int(last["count"]) <= SegmentLimit) return null;

        var data = ReadSegLocked(chatId, Text(last["id"]));
        if (data?["messages"] is not JsonArray messages || messages.Count <= SegmentLimit) return null;

        segs.RemoveAt(segs.Count - 1);
        var made = new List<int>();
        var seq = segs.Count + 1;
        for (var i = 0; i < messages.Count; i += SegmentLimit)
        {
            var chunk = new JsonArray();
            for (var j = i; j < Math.Min(i + SegmentLimit, messages.Count); j++) chunk.Add(messages[j]?.DeepClone());
            var from = Long(chunk[0]?["t"]);
            var to = Long(chunk[chunk.Count - 1]?["t"]);
            var seg = new JsonObject
            {
                ["id"] = NewSegmentId(seq++),
                ["from"] = from,
                ["to"] = to,
                ["count"] = chunk.Count,
            };
            DataStore.WriteJson(SegFile(chatId, Text(seg["id"])), new JsonObject
            {
                ["chatId"] = chatId,
                ["id"] = seg["id"]!.DeepClone(),
                ["from"] = from,
                ["to"] = to,
                ["messages"] = chunk,
            });
            segs.Add(seg);
            made.Add(chunk.Count);
        }
        WriteIndexLocked(chatId, idx);
        TryDelete(SegFile(chatId, Text(last["id"])));
        return made;
    }

    private void CancelTimer(string chatId)
    {
        if (_timers.TryRemove(chatId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    // ---------- 媒体落盘（原 extractMedia / saveMedia） ----------

    private void ExtractMediaLocked(string chatId, JsonObject msg)
    {
        if (msg["image"] is JsonValue)
        {
            var saved = SaveMediaLocked(chatId, Text(msg["image"]), Text(msg["imageMime"]));
            msg.Remove("image");
            msg["imageMime"] = "";
            if (saved is not null)
            {
                msg["mediaId"] = saved.Value.MediaId;
                msg["mediaMime"] = saved.Value.Mime;
                msg["bytes"] = saved.Value.Bytes;
            }
            else
            {
                msg["mediaExpired"] = true;
            }
        }

        if (msg["voice"] is JsonValue)
        {
            var saved = SaveMediaLocked(chatId, Text(msg["voice"]), Text(msg["voiceMime"]));
            msg.Remove("voice");
            msg["voiceMime"] = "";
            if (saved is not null)
            {
                msg["mediaId"] = saved.Value.MediaId;
                msg["mediaMime"] = saved.Value.Mime;
                msg["bytes"] = saved.Value.Bytes;
            }
            else
            {
                msg["mediaExpired"] = true;
            }
        }
    }

    /// <summary>base64（可带 data: 前缀）落盘，返回 mediaId / mime / 字节数；失败 null。</summary>
    private (string MediaId, string Mime, int Bytes)? SaveMediaLocked(string chatId, string base64, string mimeHint)
    {
        var raw = (base64 ?? "").Trim();
        var mime = (mimeHint ?? "").Split(';')[0].Trim().ToLowerInvariant();

        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            if (comma > 0)
            {
                var header = raw[5..comma];
                var semi = header.IndexOf(';');
                mime = (semi >= 0 ? header[..semi] : header).Trim().ToLowerInvariant();
                raw = raw[(comma + 1)..];
            }
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(raw); }
        catch { return null; }
        if (bytes.Length == 0) return null;

        if (mime.Length == 0 || mime == "application/octet-stream") mime = SniffMime(bytes);
        var (mediaId, count) = _media.Save(chatId, bytes, mime);
        return (mediaId, mime, count);
    }

    /// <summary>没有 MIME 提示时按魔数猜（对齐 core/image.js 的 mimeOf）。</summary>
    private static string SniffMime(byte[] b)
    {
        bool StartsWith(params byte[] sig)
        {
            if (b.Length < sig.Length) return false;
            for (var i = 0; i < sig.Length; i++) if (b[i] != sig[i]) return false;
            return true;
        }

        if (StartsWith(0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (StartsWith(0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (StartsWith(0x47, 0x49, 0x46, 0x38)) return "image/gif";
        if (b.Length > 12 && StartsWith(0x52, 0x49, 0x46, 0x46) && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (b.Length > 12 && StartsWith(0x52, 0x49, 0x46, 0x46) && b[8] == 0x57 && b[9] == 0x41 && b[10] == 0x56 && b[11] == 0x45) return "audio/wav";
        if (StartsWith(0x4F, 0x67, 0x67, 0x53)) return "audio/ogg";
        if (StartsWith(0x49, 0x44, 0x33) || StartsWith(0xFF, 0xFB)) return "audio/mpeg";
        return "application/octet-stream";
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* 删不掉下次再试 */ }
    }

    // ---------- 异步便利方法（ChatHub / 路由用） ----------

    /// <summary>追加一条（message 里可带 image/voice 的 base64，落盘后换成 mediaId）。</summary>
    public Task<JsonObject> AppendAsync(string chatId, JsonObject message, CancellationToken ct = default)
        => Task.Run(() => Append(chatId, message), ct);

    /// <summary>按原 API 的形状翻页：file=last|prev 走整片，否则按 before 逐条翻。</summary>
    public Task<JsonObject> PageAsync(string chatId, string? file, string? before, int limit, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var cursor = ParseLong(before);
            if (string.Equals(file, "last", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file, "prev", StringComparison.OrdinalIgnoreCase))
            {
                var r = PageFile(chatId, file!, cursor);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["messages"] = r.Messages,
                    ["oldestAt"] = r.OldestAt,
                    ["hasMore"] = r.HasMore,
                    ["file"] = new JsonObject
                    {
                        ["index"] = r.Index,
                        ["total"] = r.Total,
                        ["id"] = r.FileId,
                    },
                };
            }
            var take = Math.Clamp(limit <= 0 ? SegmentLimit : limit, 1, 100);
            var page = Page(chatId, cursor, take);
            return new JsonObject
            {
                ["ok"] = true,
                ["messages"] = page.Messages,
                ["oldestAt"] = page.OldestAt,
                ["hasMore"] = page.HasMore,
                ["segment"] = new JsonObject { ["limit"] = take },
            };
        }, ct);

    /// <summary>一共多少条。</summary>
    public Task<int> CountAsync(string chatId, CancellationToken ct = default)
        => Task.Run(() => TotalOf(chatId), ct);

    /// <summary>删掉一个会话的全部记录和媒体。</summary>
    public Task ClearAsync(string chatId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            _io.Wait();
            try
            {
                CancelTimer(chatId);
                _typing.TryRemove(chatId, out _);
                try { if (Directory.Exists(ChatDir(chatId))) Directory.Delete(ChatDir(chatId), true); } catch { }
                try
                {
                    var dir = _media.DirOf(chatId);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch { }
            }
            finally
            {
                _io.Release();
            }
        }, ct);

    /// <summary>按保留天数清一遍所有会话，返回有变化的会话。</summary>
    public Task<List<JsonObject>> PruneAsync(int keepDays, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (keepDays > 0 && keepDays != KeepDays())
            {
                // 显式传的天数优先：临时改配置跑一遍，完事还原
                var old = _host.Cfg["history"] is JsonObject h ? h["keepDays"]?.DeepClone() : null;
                try
                {
                    var node = new JsonObject();
                    if (_host.Cfg["history"] is JsonObject hist) node = (JsonObject)hist.DeepClone();
                    node["keepDays"] = keepDays;
                    _host.Cfg["history"] = node;
                    return SweepAll();
                }
                finally
                {
                    if (_host.Cfg["history"] is JsonObject h2) h2["keepDays"] = old?.DeepClone();
                }
            }
            return SweepAll();
        }, ct);

    /// <summary>标成已读，返回之前有多少条未读（机器人发的）。</summary>
    public Task<int> MarkReadAsync(string chatId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var unread = Unread(chatId);
            _io.Wait();
            try
            {
                var file = ReadFile(chatId);
                var state = DataStore.ReadObject(file);
                state["lastReadAt"] = Now;
                state["updatedAt"] = Now;
                DataStore.WriteJson(file, state);
            }
            finally
            {
                _io.Release();
            }
            return unread;
        }, ct);

    /// <summary>未读数：lastReadAt 之后机器人发的消息条数。</summary>
    public int Unread(string chatId)
    {
        _io.Wait();
        try
        {
            var lastRead = Long(DataStore.ReadObject(ReadFile(chatId))["lastReadAt"]);
            if (lastRead <= 0) return 0;
            var idx = ReadIndexLocked(chatId);
            var count = 0;
            foreach (var node in (JsonArray)idx["segments"]!)
            {
                if (node is not JsonObject seg || Int(seg["count"]) <= 0) continue;
                var data = ReadSegLocked(chatId, Text(seg["id"]));
                if (data?["messages"] is not JsonArray messages) continue;
                foreach (var m in messages)
                {
                    if (m is null) continue;
                    if (Long(m["t"]) <= lastRead) continue;
                    var who = Text(m["who"]);
                    if (!string.Equals(who, "user", StringComparison.Ordinal)) count++;
                }
            }
            return count;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>设置/取消「正在输入」。配置 merge.waitTypingSec 为 0 时直接取消。</summary>
    public void SetTyping(string chatId, bool on)
    {
        var hold = Math.Max(0, _host.Num(5, "merge", "waitTypingSec"));
        if (on && hold > 0) _typing[chatId] = Now + (long)(hold * 1000);
        else _typing.TryRemove(chatId, out _);
        _host.Emit("typing", new JsonObject { ["chatId"] = chatId, ["on"] = on });
    }

    public Task SetTypingAsync(string chatId, bool on, CancellationToken ct = default)
        => Task.Run(() => SetTyping(chatId, on), ct);

    /// <summary>现在是不是在「正在输入」。</summary>
    public bool GetTyping(string chatId)
        => _typing.TryGetValue(chatId, out var until) && until > Now;

    private static long ParseLong(string? text)
        => long.TryParse(text, out var n) ? n : 0;
}
