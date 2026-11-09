using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Core.Storage;

/// <summary>读一个 JSON 文件的结果状态：缺失 / 正常 / 损坏（解析失败）/ 读失败（IO、权限）。</summary>
public enum JsonReadStatus
{
    Missing,
    Ok,
    Corrupted,
    IoError,
}

/// <summary>带状态区分的读取结果。上层据此决定「当默认值用」还是「进入显式诊断，绝不静默覆盖原文件」。</summary>
public sealed record JsonReadResult(JsonReadStatus Status, JsonNode? Node, string? Error)
{
    public bool Ok => Status == JsonReadStatus.Ok;
}

/// <summary>
/// 数据目录与 JSON 读写。行为对齐原 Node 版 core/store.js：自动建目录。
/// 读有详细状态区分（缺失/损坏/IO失败，不再混为一类回退默认值）；
/// 写用唯一临时文件 + 冲盘 + 原子覆盖，坏一个写者不会弄坏另一个写者的半截文件。
/// </summary>
public sealed class DataStore
{
    public DataStore(string root, string? dataFolderName = null, string? dataDirOverride = null)
    {
        Root = root;
        DataDir = dataDirOverride ?? (dataFolderName is null ? root : Path.Combine(root, dataFolderName));
        Directory.CreateDirectory(DataDir);
    }

    /// <summary>应用根目录（存放 public/、personas/、logs/ 等）。</summary>
    public string Root { get; }

    /// <summary>数据目录（config.json、users.json、聊天分片等）。可用环境变量 AURACHAT_DATA_DIR 覆盖。</summary>
    public string DataDir { get; }

    public static string DefaultRoot()
    {
        var home = Environment.GetEnvironmentVariable("AURACHAT_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return home!;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraChat");
    }

    public static DataStore CreateDefault()
    {
        var root = DefaultRoot();
        var overrideDir = Environment.GetEnvironmentVariable("AURACHAT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return new DataStore(root, dataDirOverride: overrideDir);
        return new DataStore(root, "data");
    }

    public string PathIn(string name) => Path.Combine(DataDir, name);

    public string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static string? ReadText(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            return text;
        }
        catch
        {
            return null;
        }
    }

    public static JsonNode? ReadJson(string file, JsonNode? fallback = null)
    {
        var text = ReadText(file);
        if (text is null) return fallback;
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch { return fallback; }
    }

    /// <summary>
    /// 带状态区分的读取：Missing（没这个文件）/ Ok / Corrupted（读到了但不是合法 JSON）/ IoError（权限、占用等）。
    /// 旧 ReadJson 把四类混成「回退默认值」，坏文件会被下一次写悄悄覆盖 —— 调用方改用本方法后，
    /// 损坏必须变成显式诊断（备份原文件 + 报错），不能静默当空数据处理。
    /// </summary>
    public static JsonReadResult ReadJsonDetailed(string file)
    {
        string text;
        try
        {
            if (!File.Exists(file)) return new JsonReadResult(JsonReadStatus.Missing, null, null);
            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return new JsonReadResult(JsonReadStatus.IoError, null, ex.Message);
        }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
            return new JsonReadResult(JsonReadStatus.Ok, node, null);
        }
        catch (Exception ex)
        {
            return new JsonReadResult(JsonReadStatus.Corrupted, null, ex.Message);
        }
    }

    public static JsonObject ReadObject(string file, JsonObject? fallback = null)
        => ReadJson(file, fallback) as JsonObject ?? fallback ?? new JsonObject();

    public static JsonArray ReadArray(string file, JsonArray? fallback = null)
        => ReadJson(file, fallback) as JsonArray ?? fallback ?? new JsonArray();

    /// <summary>
    /// 写 JSON：唯一临时文件（带 pid + 随机后缀，同进程并发写同一目标不再争同一个 tmp）
    /// → 冲盘（flushToDisk，掉电后 tmp 里也是完整内容）→ 原子覆盖。
    /// 覆盖前按目标文件路径取一把跨进程命名互斥锁（Windows）：多个进程/线程写同一文件时
    /// 严格串行，谁的 Move 都不会撞上谁；不同目标互不阻塞。非 Windows 平台退回有界重试。
    /// 任何时刻目标文件都是某一个写者的完整内容，绝不会是拼起来的半截。
    /// </summary>
    public static void WriteJson(string file, JsonNode? value)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{file}.tmp-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            WithTargetLock(file, () => MoveWithRetry(tmp, file));
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清不掉就留着，读取方会按后缀忽略 */ }
            throw;
        }
    }

    /// <summary>
    /// 同一目标文件的跨进程写串行锁（名字取路径的 SHA-256 前缀，避免非法字符）。
    /// 尽力而为：互斥体建不出来/等不到（受限环境、超时）都不阻断写入 —— 单条 Move 本身原子，
    /// 还有 MoveWithRetry 有界重试兜底，锁只是省掉无谓的重试冲突。
    /// </summary>
    private static void WithTargetLock(string target, Action body)
    {
        if (!OperatingSystem.IsWindows()) { body(); return; }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target.ToLowerInvariant()));
        var name = "Global\\AuraChat.Store." + Convert.ToHexString(hash, 0, 12);
        Mutex mux;
        try { mux = new Mutex(false, name); }
        catch { body(); return; }   // 全局互斥体不可用（受限沙箱等）：退回原子 Move 本身的并发安全
        using (mux)
        {
            var held = false;
            try { held = mux.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; }   // 上一个持有者崩了，锁已归我们
            catch { held = false; }   // 等待出别的错也不挡写
            try { body(); }
            finally
            {
                if (held)
                {
                    try { mux.ReleaseMutex(); } catch (ApplicationException) { /* 没拿到锁就不用放 */ }
                }
            }
        }
    }

    /// <summary>File.Move 偶发与杀软/备份的短暂冲突，有界重试兜底（正常情况下互斥锁已把并发挡住）。</summary>
    private static void MoveWithRetry(string from, string to, int attempts = 5)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (i < attempts - 1 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(40 * (i + 1));
            }
        }
    }

    /// <summary>目标文件的临时文件是否应该被忽略（写残骸不参与任何读取/重建扫描）。</summary>
    public static bool IsTempFile(string path)
        => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal);

    public static string ToJson(JsonNode? value) => value?.ToJsonString() ?? "null";
}
