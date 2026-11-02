using System.Collections.Concurrent;
using System.Text;

namespace AuraChat.Core.Logging;

/// <summary>日志条目（api/logs 返回给前端的就是这个形状）。</summary>
public sealed record LogEntry(long T, string Time, string Level, string Area, string Text);

/// <summary>
/// 内存环形日志 + 滚动文件。对齐原 Node 版 core/logger.js：
/// 保留最近 500 条 / 按配置体积裁剪、可订阅、可清空、可按级别和区域过滤。
/// </summary>
public sealed class AppLog
{
    private const int RingLimit = 500;
    private static readonly string[] Levels = ["debug", "info", "warn", "error"];

    private readonly LinkedList<LogEntry> _ring = new();
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<int, Action<LogEntry>> _listeners = new();
    private int _nextListener;
    private long _ringBytes;
    private int _dropped;
    private Func<long> _maxBytes = () => 2L * 1024 * 1024;
    private string? _file;
    private readonly Lock _fileGate = new();

    public void SetMaxBytes(Func<long> maxBytes) => _maxBytes = maxBytes;

    /// <summary>可选：写到磁盘的日志文件（%APPDATA%\AuraChat\logs\aurachat.log）。</summary>
    public void AttachFile(string path)
    {
        _file = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string? FilePath => _file;

    public static int Rank(string level)
    {
        var i = Array.IndexOf(Levels, level);
        return i < 0 ? 1 : i;
    }

    public LogEntry Write(string level, string area, string message)
    {
        var text = Redact.Apply(message);
        var now = DateTime.Now;
        var entry = new LogEntry(
            new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            now.ToString("HH:mm:ss"),
            level,
            area,
            text);

        lock (_gate)
        {
            _ring.AddLast(entry);
            _ringBytes += SizeOf(entry);
            Trim();
        }

        if (level != "debug") WriteFile(entry);

        foreach (var kv in _listeners)
        {
            try { kv.Value(entry); } catch { /* 订阅者自己炸了不影响别人 */ }
        }
        return entry;
    }

    private void Trim()
    {
        var max = Math.Max(64 * 1024, _maxBytes());
        while (_ring.Count > 1 && _ringBytes > max)
        {
            _ringBytes -= SizeOf(_ring.First!.Value);
            _ring.RemoveFirst();
            _dropped++;
        }
        while (_ring.Count > RingLimit)
        {
            _ringBytes -= SizeOf(_ring.First!.Value);
            _ring.RemoveFirst();
            _dropped++;
        }
    }

    private static long SizeOf(LogEntry e) => 64 + (e.Text?.Length ?? 0) * 2L;

    private void WriteFile(LogEntry e)
    {
        var file = _file;
        if (file is null) return;
        lock (_fileGate)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > Math.Max(256 * 1024, _maxBytes()))
                {
                    var bak = file + ".1";
                    File.Move(file, bak, overwrite: true);
                }
                File.AppendAllText(file, $"{e.Time} [{e.Level}] {e.Area}: {e.Text}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch { /* 日志写不进去不能影响主流程 */ }
        }
    }

    public IReadOnlyList<LogEntry> Recent(int limit = 200, string? level = null, string? area = null)
    {
        lock (_gate)
        {
            var min = level is null ? -1 : Rank(level);
            return _ring
                .Where(e => (min < 0 || Rank(e.Level) >= min) && (area is null || e.Area == area))
                .TakeLast(Math.Max(1, limit))
                .ToList();
        }
    }

    public (int Count, long Bytes, int Dropped) Stats()
    {
        lock (_gate) return (_ring.Count, _ringBytes, _dropped);
    }

    public int Clear()
    {
        lock (_gate)
        {
            var n = _ring.Count;
            _ring.Clear();
            _ringBytes = 0;
            _dropped = 0;
            return n;
        }
    }

    public IDisposable Subscribe(Action<LogEntry> onEntry)
    {
        var id = Interlocked.Increment(ref _nextListener);
        _listeners[id] = onEntry;
        return new Subscription(() => _listeners.TryRemove(id, out _));
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>给某个模块用的带区域名的 logger。</summary>
    public AreaLog Child(string area) => new(this, area);

    // 没有指定区域时的快捷写法（区域记成 "app"）。
    public void Debug(params object?[] parts) => Child("app").Debug(parts);
    public void Info(params object?[] parts) => Child("app").Info(parts);
    public void Warn(params object?[] parts) => Child("app").Warn(parts);
    public void Error(params object?[] parts) => Child("app").Error(parts);
}

/// <summary>按区域（core / memory / hub …）打日志的轻量包装。</summary>
public sealed class AreaLog(AppLog log, string area)
{
    public string Area => area;
    public void Debug(params object?[] parts) => log.Write("debug", area, Join(parts));
    public void Info(params object?[] parts) => log.Write("info", area, Join(parts));
    public void Warn(params object?[] parts) => log.Write("warn", area, Join(parts));
    public void Error(params object?[] parts) => log.Write("error", area, Join(parts));

    private static string Join(object?[] parts) => string.Join(" ", parts.Select(p => p switch
    {
        null => "",
        string s => s,
        Exception e => e.Message,
        _ => SafeJson(p),
    }));

    private static string SafeJson(object value)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(value); }
        catch { return value.ToString() ?? ""; }
    }
}
