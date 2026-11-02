using System.Text;
using System.Text.RegularExpressions;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Life;

/// <summary>一个技能（SKILL.md）的目录信息。</summary>
public sealed record SkillInfo(string Name, string Description, string File);

/// <summary>
/// 自带技能：扫 `<数据目录>/workspace/.dsh/skills/&lt;名字&gt;/SKILL.md`，
/// 解析 frontmatter，把目录塞进提示词，use_skill 时返回正文。
/// 语义逐条对齐原 features/skills.js（目录不存在就返回空，不报错）。
/// </summary>
public sealed partial class SkillService
{
    private readonly DataStore _store;
    private readonly AreaLog _log;

    public SkillService(DataStore store, AreaLog log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>技能根目录（只有这个 workspace 下才扫得到）。</summary>
    public string Root => Path.Combine(_store.DataDir, "workspace", ".dsh", "skills");

    public List<SkillInfo> List()
    {
        var dir = Root;
        if (!Directory.Exists(dir)) return [];

        DirectoryInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetDirectories();
        }
        catch (Exception ex)
        {
            _log.Warn("读技能目录失败：", ex.Message);
            return [];
        }

        var output = new List<SkillInfo>();
        foreach (var entry in entries)
        {
            var file = Path.Combine(entry.FullName, "SKILL.md");
            if (!File.Exists(file)) continue;

            string text;
            try
            {
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _log.Warn("读技能文件失败：", entry.Name, ex.Message);
                continue;
            }

            var (meta, _) = ParseFrontmatter(text);
            var name = (meta.GetValueOrDefault("name") ?? entry.Name).Trim();
            if (!NameRe().IsMatch(name))
            {
                _log.Warn($"技能名不合法，跳过：{entry.Name} → {name}");
                continue;
            }
            var description = (meta.GetValueOrDefault("description") ?? "").Trim();
            if (description.Length == 0)
            {
                _log.Warn($"技能缺 description，跳过：{name}");
                continue;
            }
            output.Add(new SkillInfo(name, description, file));
        }
        return output.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>读某个技能的正文（去掉 frontmatter）；没有返回 null。</summary>
    public SkillInfo? Read(string? name)
    {
        var want = (name ?? "").Trim();
        var hit = List().FirstOrDefault(s => s.Name == want);
        if (hit is null) return null;
        try
        {
            var text = File.ReadAllText(hit.File, Encoding.UTF8);
            var (_, body) = ParseFrontmatter(text);
            return hit with { Description = body.Trim() };
        }
        catch (Exception ex)
        {
            _log.Warn("读技能正文失败：", want, ex.Message);
            return null;
        }
    }

    /// <summary>进系统提示词的目录。没有技能时返回空串，不占上下文。</summary>
    public string ContextLine()
    {
        var items = List();
        if (items.Count == 0) return "";
        var lines = items.Take(12).Select(s => $"- {s.Name}：{s.Description}");
        return "（你另外还会这些本事。要用的时候，先用 use_skill 把它调出来看清楚步骤再动手：\n"
               + string.Join('\n', lines) + "）";
    }

    /// <summary>解析 YAML 风格 frontmatter：只认 `key: value` 一行一条，值去首尾引号。</summary>
    internal static (Dictionary<string, string> Meta, string Body) ParseFrontmatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var m = FrontmatterRe().Match(text);
        if (!m.Success) return (meta, text);
        foreach (var rawLine in m.Groups[1].Value.Split('\n'))
        {
            var kv = KeyLineRe().Match(rawLine.TrimEnd('\r'));
            if (!kv.Success) continue;
            var value = kv.Groups[2].Value.Trim();
            if (value.Length >= 2)
            {
                var a = value[0];
                var b = value[^1];
                if ((a == '"' && b == '"') || (a == '\'' && b == '\'')) value = value[1..^1];
            }
            meta[kv.Groups[1].Value] = value;
        }
        return (meta, text[m.Length..]);
    }

    [GeneratedRegex(@"^---\r?\n([\s\S]*?)\r?\n---\r?\n?")]
    private static partial Regex FrontmatterRe();

    [GeneratedRegex(@"^([A-Za-z_][\w-]*)\s*:\s*(.*)$")]
    private static partial Regex KeyLineRe();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex NameRe();
}
