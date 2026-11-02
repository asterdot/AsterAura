using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Life;

/// <summary>
/// 聊天日志（`&lt;数据目录&gt;/chatlog/&lt;yyyy-MM-dd&gt;.jsonl`，每行 `{t,chatId,who,text,uid?}`）。
/// 原版由 chat/hub.js 的 logChat 写，记忆巩固只读；这里默认只读，不抢写入权。
/// </summary>
public static partial class ChatLog
{
    /// <summary>
    /// 是否由记忆模块补写聊天日志。默认 false：ChatHub 会写，两边都写会出现重复行。
    /// 只有确认没人写的时候才打开（仅影响 ObserveAsync）。
    /// </summary>
    public static bool MirrorEnabled { get; set; }

    /// <summary>本地日期键，与记忆 / 空间日记的 dayKey 一致（UTC+8 凌晨不会写进昨天的文件）。</summary>
    public static string DayKey(DateTime? when = null)
    {
        var d = when ?? DateTime.Now;
        return $"{d.Year:0000}-{d.Month:00}-{d.Day:00}";
    }

    public static string Dir(DataStore store) => store.PathIn("chatlog");

    /// <summary>有日志的日期（升序；目录不存在返回空）。</summary>
    public static List<string> Days(DataStore store)
    {
        var dir = Dir(store);
        if (!Directory.Exists(dir)) return [];
        try
        {
            return new DirectoryInfo(dir).GetFiles("*.jsonl")
                .Select(f => f.Name)
                .Where(n => DayFileRe().IsMatch(n))
                .Select(n => n[..10])
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>读一天的日志行（坏行跳过）。</summary>
    public static JsonArray Read(DataStore store, string day)
    {
        var output = new JsonArray();
        try
        {
            var file = Path.Combine(Dir(store), day + ".jsonl");
            if (!File.Exists(file)) return output;
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject obj) output.Add(obj);
                }
                catch
                {
                    // 半截行（进程被杀）直接跳过
                }
            }
        }
        catch
        {
            // 读不到就当没有，不能影响整理
        }
        return output;
    }

    public static void Delete(DataStore store, string day)
    {
        try
        {
            var file = Path.Combine(Dir(store), day + ".jsonl");
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // 删不掉下轮再试
        }
    }

    /// <summary>追加一行（默认不启用，见 MirrorEnabled）。</summary>
    public static void Append(DataStore store, string chatId, string who, string text, string? uid = null)
    {
        if (!MirrorEnabled) return;
        try
        {
            var day = DayKey();
            var dir = Dir(store);
            Directory.CreateDirectory(dir);
            var line = new JsonObject
            {
                ["t"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["chatId"] = chatId,
                ["who"] = who,
                ["text"] = text,
            };
            if (!string.IsNullOrEmpty(uid)) line["uid"] = uid;
            File.AppendAllText(Path.Combine(dir, day + ".jsonl"), line.ToJsonString() + "\n", new UTF8Encoding(false));
        }
        catch
        {
            // 落盘失败不能影响对话
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\.jsonl$")]
    private static partial Regex DayFileRe();
}
