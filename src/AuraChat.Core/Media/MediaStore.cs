using System.Text.Json.Nodes;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Core.Media;

/// <summary>
/// 聊天里的图片 / 语音 / 表情落盘（对齐原 history.js 的 media/&lt;chatId&gt;/&lt;mediaId&gt; 布局）。
/// mediaId = uuid + '.' + 扩展名，消息对象里只存 mediaId，二进制在磁盘上。
/// </summary>
public sealed class MediaStore(DataStore store, AppLog log)
{
    private static readonly Dictionary<string, string> MimeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/jpg"] = "jpg",
        ["image/webp"] = "webp",
        ["image/gif"] = "gif",
        ["audio/mpeg"] = "mp3",
        ["audio/mp3"] = "mp3",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
        ["audio/ogg"] = "ogg",
        ["audio/webm"] = "webm",
    };

    private static readonly Dictionary<string, string> ExtToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["webp"] = "image/webp",
        ["gif"] = "image/gif",
        ["mp3"] = "audio/mpeg",
        ["wav"] = "audio/wav",
        ["ogg"] = "audio/ogg",
        ["webm"] = "audio/webm",
        ["m4a"] = "audio/mp4",
    };

    private readonly AreaLog _log = log.Child("media");

    /// <summary>某个会话的媒体目录。</summary>
    public string DirOf(string chatId)
    {
        // 原版 history.js:44 mediaPath() 用的是 dataDir('media')，迁移时要跟老数据对上
        var dir = Path.Combine(store.DataDir, "media", SafeName(chatId));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>存一段二进制，返回 mediaId（uuid.ext）。</summary>
    public (string MediaId, int Bytes) Save(string chatId, byte[] data, string mime)
    {
        var ext = MimeToExt.TryGetValue(mime, out var e) ? e : "bin";
        var mediaId = Guid.NewGuid().ToString("D") + "." + ext;
        var path = Path.Combine(DirOf(chatId), mediaId);
        File.WriteAllBytes(path, data);
        _log.Debug($"媒体落盘 {chatId}/{mediaId} {data.Length} 字节");
        return (mediaId, data.Length);
    }

    /// <summary>媒体文件路径；不存在或名字不合法返回 null。</summary>
    public string? PathOf(string chatId, string mediaId)
    {
        var id = SafeName(mediaId);
        if (id.Length == 0) return null;
        var path = Path.Combine(DirOf(chatId), id);
        return File.Exists(path) ? path : null;
    }

    public void Delete(string chatId, string mediaId)
    {
        try
        {
            var path = PathOf(chatId, mediaId);
            if (path is not null) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"删媒体失败 {mediaId}：{ex.Message}");
        }
    }

    /// <summary>扩展名 → MIME（对齐原 history.mimeOfExt）。</summary>
    public static string MimeOfExt(string ext)
        => ExtToMime.TryGetValue(ext.TrimStart('.'), out var m) ? m : "application/octet-stream";

    /// <summary>mediaId 后缀的扩展名（小写，不含点）。</summary>
    public static string ExtOfMediaId(string mediaId)
    {
        var i = mediaId.LastIndexOf('.');
        return i >= 0 && i < mediaId.Length - 1 ? mediaId[(i + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>整个媒体目录占了多少字节（状态面板用）。</summary>
    public (int Files, long Bytes) Stats()
    {
        var root = Path.Combine(store.Root, "media");
        if (!Directory.Exists(root)) return (0, 0);
        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                files++;
                bytes += new FileInfo(f).Length;
            }
        }
        catch { /* 统计而已，出错就当没有 */ }
        return (files, bytes);
    }

    public JsonObject StatsJson()
    {
        var (files, bytes) = Stats();
        return new JsonObject { ["files"] = files, ["bytes"] = bytes };
    }

    /// <summary>只留安全字符，防目录穿越。</summary>
    public static string SafeName(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        var s = new string(chars).Trim('.');
        return s.Length > 120 ? s[..120] : s;
    }
}
