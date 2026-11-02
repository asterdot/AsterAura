using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Media;

/// <summary>
/// 表情包库（逐条照原 features/stickers.js）：<c>&lt;数据目录&gt;/stickers/</c> 下放图片，
/// <c>index.json</c> 存 <c>{items:[...]}</c>，文件名一律 <c>manual-&lt;md5&gt;.&lt;ext&gt;</c>。
/// 这里只做库和挑选，不负责把表情塞进回复气泡（那是聊天层的事）。
/// </summary>
public sealed class StickerService : IStickerService
{
    /// <summary>原版默认标签：挑表情时优先按这些词匹配回复正文。</summary>
    public static readonly string[] DefaultTags =
        ["开心", "卖萌", "生气", "委屈", "无语", "嘲讽", "疑惑", "早晚安"];

    private readonly FeatureHost _host;
    private readonly AreaLog _log;
    private readonly Lock _lock = new();

    public StickerService(FeatureHost host)
    {
        _host = host;
        _log = host.Log.Child("stickers");
    }

    /// <summary>表情库目录。</summary>
    public string Dir => _host.Store.PathIn("stickers");

    /// <summary>索引文件。</summary>
    public string IndexFile => Path.Combine(Dir, "index.json");

    // ---------------- 库读写 ----------------

    /// <summary>读库。文件坏了就给空库，不抛。</summary>
    public JsonObject LoadLibrary()
    {
        var lib = DataStore.ReadObject(IndexFile, new JsonObject { ["items"] = new JsonArray() });
        if (lib["items"] is not JsonArray) lib["items"] = new JsonArray();
        return lib;
    }

    private void SaveLibrary(JsonObject lib)
    {
        _host.Store.EnsureDir(Dir);
        DataStore.WriteJson(IndexFile, lib);
    }

    private static JsonArray ItemsOf(JsonObject lib) => (JsonArray)lib["items"]!;

    private static IEnumerable<JsonObject> Objects(JsonArray items)
        => items.OfType<JsonObject>();

    private static string FileOf(JsonObject item) => item["file"]?.ToString() ?? "";

    private static string Md5Of(JsonObject item) => item["md5"]?.ToString() ?? "";

    private static string NameOf(JsonObject item) => item["name"]?.ToString() ?? "";

    private static List<string> TagsOf(JsonObject item)
    {
        var tags = new List<string>();
        if (item["tags"] is JsonArray arr)
            foreach (var tag in arr)
            {
                var text = tag?.ToString() ?? "";
                if (text.Length > 0) tags.Add(text);
            }
        return tags;
    }

    /// <summary>按 md5 找一张（不存在返回 null）。</summary>
    public JsonObject? ItemOf(string? md5)
    {
        if (string.IsNullOrWhiteSpace(md5)) return null;
        return Objects(ItemsOf(LoadLibrary()))
            .FirstOrDefault(item => string.Equals(Md5Of(item), md5.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>库里一共多少张。</summary>
    public int Count => ItemsOf(LoadLibrary()).Count;

    // ---------------- 匹配打分（原 matchSticker） ----------------

    /// <summary>按名字/标签给表情打分，bestScore &gt;= 1.5 才算命中。</summary>
    public static JsonObject? MatchSticker(JsonArray items, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0 || items.Count == 0) return null;

        JsonObject? best = null;
        var bestScore = 0.0;
        foreach (var item in Objects(items))
        {
            var hay = new List<string> { NameOf(item) };
            hay.AddRange(TagsOf(item));

            var score = 0.0;
            foreach (var h in hay)
            {
                if (h.Length == 0) continue;
                double s;
                if (h == q) s = 10;
                else if (h.Contains(q, StringComparison.Ordinal) || q.Contains(h, StringComparison.Ordinal)) s = 6;
                else
                {
                    var common = q.Distinct().Count(ch => h.Contains(ch));
                    s = common >= 2 ? common / (double)Math.Max(q.Length, 1) * 4 : 0;
                }

                if (s > score) score = s;
            }

            if (score > bestScore || (Math.Abs(score - bestScore) < 1e-9 && score > 0 && Random.Shared.NextDouble() < 0.5))
            {
                best = item;
                bestScore = score;
            }
        }

        return bestScore >= 1.5 ? best : null;
    }

    /// <summary>给一条回复挑表情：先按默认标签命中，再按整句匹配，最后随机（原 pickForReply）。</summary>
    public JsonObject? PickForReply(string? replyText)
    {
        var items = ItemsOf(LoadLibrary());
        if (items.Count == 0) return null;

        var text = replyText ?? "";
        var words = new List<string>(DefaultTags);
        foreach (var item in Objects(items)) words.AddRange(TagsOf(item));

        foreach (var word in words)
        {
            if (word.Length == 0 || !text.Contains(word, StringComparison.Ordinal)) continue;
            var hit = MatchSticker(items, word);
            if (hit is not null) return hit;
        }

        var list = Objects(items).ToList();
        return list.Count == 0 ? null : list[Random.Shared.Next(list.Count)];
    }

    /// <summary>挑一张该淘汰的：非手工/面板来源里用得最少、最久没用的（原 pickEvict）。</summary>
    public static JsonObject? PickEvict(JsonArray items)
    {
        var pool = Objects(items)
            .Where(item =>
            {
                var from = item["from"]?.ToString() ?? "";
                return from != "manual" && from != "panel";
            })
            .OrderBy(item => JsonUtil.Num(item["uses"], 0))
            .ThenBy(item =>
            {
                var lastUsed = JsonUtil.Num(item["lastUsed"], 0);
                return lastUsed > 0 ? lastUsed : JsonUtil.Num(item["at"], 0);
            })
            .ToList();
        return pool.Count == 0 ? null : pool[0];
    }

    // ---------------- 入库（原 addFromBuffer） ----------------

    private JsonObject AddFromBuffer(byte[] buffer, string? name, IEnumerable<string>? tags, string? from, string? hint, string? forceMd5 = null)
    {
        lock (_lock)
        {
            var md5 = string.IsNullOrWhiteSpace(forceMd5) ? MediaUtil.Md5Hex(buffer) : forceMd5.Trim().ToLowerInvariant();
            var lib = LoadLibrary();
            var items = ItemsOf(lib);

            var exist = Objects(items).FirstOrDefault(item =>
                string.Equals(Md5Of(item), md5, StringComparison.OrdinalIgnoreCase));
            if (exist is not null) return new JsonObject { ["ok"] = true, ["item"] = exist.DeepClone(), ["existed"] = true };

            var mime = MediaUtil.ImageMime(buffer);
            if (!mime.StartsWith("image/", StringComparison.Ordinal)) return Fail("不是图片");

            var size = MediaUtil.ImageSize(buffer);
            var ext = MediaUtil.ExtOfImageMime(mime);
            var file = $"manual-{md5}.{ext}";
            _host.Store.EnsureDir(Dir);
            File.WriteAllBytes(Path.Combine(Dir, file), buffer);

            var item = new JsonObject
            {
                ["md5"] = md5,
                ["file"] = file,
                ["name"] = MediaUtil.Slice((name ?? "").Trim(), 12),
                ["tags"] = new JsonArray(TagsOf(tags).Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray()),
                ["from"] = string.IsNullOrWhiteSpace(from) ? "manual" : from,
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["width"] = size.Width,
                ["height"] = size.Height,
                ["bytes"] = buffer.Length,
            };
            items.Add(item);
            SaveLibrary(lib);
            _log.Info($"收了一张表情 {md5[..Math.Min(8, md5.Length)]}（{size.Width}x{size.Height}）");

            // 原版这里会拿多模态模型看图起名；C# 侧还没有看图能力，降级成描述或「表情」
            if (NameOf(item).Length == 0)
            {
                var label = MediaUtil.Slice((hint ?? "").Trim(), 12);
                if (label.Length == 0) label = "表情";
                item["name"] = label;
                SaveLibrary(lib);
            }

            TrimInternal();
            return new JsonObject { ["ok"] = true, ["item"] = item.DeepClone() };
        }
    }

    private static List<string> TagsOf(IEnumerable<string>? tags)
        => (tags ?? [])
            .Select(tag => MediaUtil.Slice((tag ?? "").Trim(), 6))
            .Where(tag => tag.Length > 0)
            .Distinct()
            .Take(6)
            .ToList();

    private void TrimInternal()
    {
        var max = _host.Int(300, "stickers", "maxLibrary");
        for (var guard = 0; guard < 20; guard++)
        {
            var lib = LoadLibrary();
            var items = ItemsOf(lib);
            if (items.Count <= max) return;

            var victim = PickEvict(items);
            if (victim is null) return;

            _log.Info($"表情库满了（上限 {max}），清掉一张不常用的：{NameOf(victim)}");
            RemoveInternal(Md5Of(victim), save: false);
        }
    }

    private bool RemoveInternal(string md5, bool save = true)
    {
        var lib = LoadLibrary();
        var items = ItemsOf(lib);
        var item = Objects(items).FirstOrDefault(x =>
            string.Equals(Md5Of(x), md5, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;

        var file = FileOf(item);
        if (file.Length > 0)
        {
            try { File.Delete(Path.Combine(Dir, file)); } catch { }
        }
        items.Remove(item);
        if (save) SaveLibrary(lib);
        return true;
    }

    /// <summary>记一次使用（原 markUsed）。</summary>
    public void MarkUsed(string? md5)
    {
        if (string.IsNullOrWhiteSpace(md5)) return;
        lock (_lock)
        {
            var lib = LoadLibrary();
            var item = Objects(ItemsOf(lib)).FirstOrDefault(x =>
                string.Equals(Md5Of(x), md5, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            item["uses"] = JsonUtil.Int(item["uses"], 0) + 1;
            item["lastUsed"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveLibrary(lib);
        }
    }

    // ---------------- IStickerService ----------------

    /// <summary>
    /// 给回复配表情：先掷 autoChance（乘心情系数），中了再按正文匹配。
    /// 原版 maxPerHour 在代码里从来没被用过，这里也照旧不限流。
    /// </summary>
    public Task<string?> PickAsync(string botText, CancellationToken ct = default)
    {
        if (!_host.Flag(true, "stickers", "enabled")) return Task.FromResult<string?>(null);

        var factor = _host.Get<IMoodService>()?.StickerFactor() ?? 1.0;
        var chance = _host.Num(0.35, "stickers", "autoChance") * factor;
        if (Random.Shared.NextDouble() >= chance) return Task.FromResult<string?>(null);

        var pick = PickForReply(botText);
        if (pick is null) return Task.FromResult<string?>(null);

        MarkUsed(Md5Of(pick));
        return Task.FromResult<string?>(FileOf(pick));
    }

    public string? RandomSticker()
    {
        var list = Objects(ItemsOf(LoadLibrary())).ToList();
        if (list.Count == 0) return null;
        var item = list[Random.Shared.Next(list.Count)];
        return FileOf(item);
    }

    public Task<JsonObject> ListAsync()
    {
        var items = new JsonArray();
        foreach (var item in Objects(ItemsOf(LoadLibrary())))
        {
            items.Add(new JsonObject
            {
                ["md5"] = Md5Of(item),
                ["name"] = NameOf(item),
                ["tags"] = item["tags"]?.DeepClone() ?? new JsonArray(),
                ["from"] = item["from"]?.ToString() ?? "",
                ["at"] = JsonUtil.Num(item["at"], 0),
                ["width"] = JsonUtil.Int(item["width"], 0),
                ["height"] = JsonUtil.Int(item["height"], 0),
                ["bytes"] = JsonUtil.Int(item["bytes"], 0),
                ["uses"] = JsonUtil.Int(item["uses"], 0),
            });
        }
        return Task.FromResult(new JsonObject { ["ok"] = true, ["items"] = items });
    }

    public Task<JsonObject> AddAsync(string md5, byte[] bytes, string? label)
        => Task.FromResult(AddFromBuffer(bytes, label, null, "manual", label, md5));

    /// <summary>md5 → 磁盘路径；库里没有或文件丢了都返回 null（路由据此给中文原因）。</summary>
    public string? PathOf(string md5)
    {
        var item = ItemOf(md5);
        if (item is null) return null;
        var path = Path.Combine(Dir, FileOf(item));
        return File.Exists(path) ? path : null;
    }

    /// <summary>上传一张表情（原 stickers.addFromBuffer；from=steal 时受偷表情开关约束）。</summary>
    public Task<JsonObject> UploadAsync(byte[] data, string? name, IEnumerable<string>? tags, string? from, string? hint)
    {
        var source = string.IsNullOrWhiteSpace(from) ? "manual" : from.Trim();
        if (source == "steal" && !_host.Flag(true, "stickers", "steal"))
            return Task.FromResult(Fail("偷表情开关关着"));
        if (data is null || data.Length == 0) return Task.FromResult(Fail("没有图片数据"));

        return Task.FromResult(AddFromBuffer(data, name, tags, source, hint));
    }

    public Task<JsonObject> RemoveAsync(string md5)
    {
        if (string.IsNullOrWhiteSpace(md5)) return Task.FromResult(Fail("没有这张"));
        lock (_lock)
        {
            var ok = RemoveInternal(md5.Trim());
            return Task.FromResult(ok ? new JsonObject { ["ok"] = true } : Fail("没有这张"));
        }
    }

    public Task<JsonObject> RenameAsync(string md5, string? name, IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(md5)) return Task.FromResult(new JsonObject { ["ok"] = false });
        lock (_lock)
        {
            var lib = LoadLibrary();
            var item = Objects(ItemsOf(lib)).FirstOrDefault(x =>
                string.Equals(Md5Of(x), md5.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null) return Task.FromResult(new JsonObject { ["ok"] = false });

            if (name is not null) item["name"] = MediaUtil.Slice(name.Trim(), 12);
            if (tags is not null)
            {
                var arr = new JsonArray();
                foreach (var tag in TagsOf(tags)) arr.Add(tag);
                item["tags"] = arr;
            }
            SaveLibrary(lib);
            return Task.FromResult(new JsonObject { ["ok"] = true, ["item"] = item.DeepClone() });
        }
    }

    public JsonNode? Snapshot()
    {
        var items = ItemsOf(LoadLibrary());
        var bytes = Objects(items).Sum(item => JsonUtil.Num(item["bytes"], 0));
        return new JsonObject
        {
            ["count"] = items.Count,
            ["max"] = _host.Int(300, "stickers", "maxLibrary"),
            ["bytes"] = (long)bytes,
        };
    }

    // ---------------- 给工具用 ----------------

    /// <summary>按名字/标签/文件名模糊找一张，返回 (条目, 绝对路径)。</summary>
    public (JsonObject Item, string Path)? Resolve(string? tagOrName)
    {
        var lib = LoadLibrary();
        var hit = MatchSticker(ItemsOf(lib), tagOrName);
        if (hit is null) return null;
        var path = Path.Combine(Dir, FileOf(hit));
        return File.Exists(path) ? (hit, path) : null;
    }

    /// <summary>库里所有条目（工具列表用）。</summary>
    public List<JsonObject> AllItems() => Objects(ItemsOf(LoadLibrary())).ToList();

    private static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };
}
