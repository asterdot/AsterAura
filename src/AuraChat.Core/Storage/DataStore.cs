using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuraChat.Core.Storage;

/// <summary>
/// 数据目录与 JSON 读写。行为对齐原 Node 版 core/store.js：
/// 读失败给默认值、写用临时文件 + 覆盖（原子替换）、自动建目录。
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

    public static JsonObject ReadObject(string file, JsonObject? fallback = null)
        => ReadJson(file, fallback) as JsonObject ?? fallback ?? new JsonObject();

    public static JsonArray ReadArray(string file, JsonArray? fallback = null)
        => ReadJson(file, fallback) as JsonArray ?? fallback ?? new JsonArray();

    /// <summary>写 JSON：先写 .tmp 再替换，避免半截文件。</summary>
    public static void WriteJson(string file, JsonNode? value)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{file}.tmp-{Environment.ProcessId}";
        File.WriteAllText(tmp, value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }

    public static string ToJson(JsonNode? value) => value?.ToJsonString() ?? "null";
}
