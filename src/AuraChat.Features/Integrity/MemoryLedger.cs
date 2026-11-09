using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Integrity;

/// <summary>
/// 记忆账本：记录所有聊天消息、记忆条目和压缩摘要的哈希链，
/// 确保"记忆≠人格"原则——原文永久保留，篡改可被检测。
/// 
/// 账本格式（纯文本，易于人工检查）：
/// timestamp|type|id|content_hash|prev_hash
/// 
/// 类型：msg（聊天消息）、mem（记忆条目）、compress（压缩摘要）
/// </summary>
public sealed class MemoryLedger
{
    private readonly string _filePath;
    private readonly AreaLog _log;
    private readonly object _gate = new();
    private string _lastHash = "0000000000000000";

    public MemoryLedger(DataStore store, AreaLog log)
    {
        // 决策：放在 data/integrity/ 目录，与业务数据分离
        var integrityDir = Path.Combine(store.DataDir, "integrity");
        if (!Directory.Exists(integrityDir))
            Directory.CreateDirectory(integrityDir);
        
        _filePath = Path.Combine(integrityDir, "memory.ledger");
        _log = log;
        
        // 启动时读取最后一个哈希
        LoadLastHash();
    }

    /// <summary>账本文件路径</summary>
    public string FilePath => _filePath;

    /// <summary>最后一个哈希值</summary>
    public string LastHash
    {
        get { lock (_gate) return _lastHash; }
    }

    /// <summary>
    /// 记录聊天消息到账本
    /// </summary>
    public void RecordMessage(string messageId, JsonObject message)
    {
        var contentHash = ComputeContentHash(message.ToJsonString());
        RecordEntry("msg", messageId, contentHash);
    }

    /// <summary>
    /// 记录记忆条目到账本
    /// </summary>
    public void RecordMemory(string memoryId, JsonObject memory)
    {
        var contentHash = ComputeContentHash(memory.ToJsonString());
        RecordEntry("mem", memoryId, contentHash);
    }

    /// <summary>
    /// 记录压缩摘要到账本
    /// </summary>
    public void RecordCompress(string segmentId, string summary)
    {
        var contentHash = ComputeContentHash(summary);
        RecordEntry("compress", segmentId, contentHash);
    }

    /// <summary>
    /// 验证账本完整性
    /// </summary>
    public VerificationResult Verify()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new VerificationResult { IsValid = true, Message = "账本文件不存在（首次运行）" };

            var entries = ReadAllEntries();
            if (entries.Count == 0)
                return new VerificationResult { IsValid = true, Message = "账本为空" };

            // 验证哈希链
            var expectedPrevHash = "0000000000000000";
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.PrevHash != expectedPrevHash)
                {
                    return new VerificationResult
                    {
                        IsValid = false,
                        Message = $"哈希链断裂：第 {i + 1} 条记录的 prev_hash 不匹配",
                        BrokenAt = i,
                        ExpectedHash = expectedPrevHash,
                        ActualHash = entry.PrevHash
                    };
                }

                // 验证当前记录的哈希
                var computedHash = ComputeEntryHash(entry.Timestamp, entry.Type, entry.Id, entry.ContentHash, entry.PrevHash);
                expectedPrevHash = computedHash;
            }

            return new VerificationResult
            {
                IsValid = true,
                Message = $"账本完整，共 {entries.Count} 条记录",
                TotalEntries = entries.Count
            };
        }
    }

    /// <summary>
    /// 获取账本统计信息
    /// </summary>
    public LedgerStats GetStats()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return new LedgerStats();

            var entries = ReadAllEntries();
            var stats = new LedgerStats
            {
                TotalEntries = entries.Count,
                MessageCount = entries.Count(e => e.Type == "msg"),
                MemoryCount = entries.Count(e => e.Type == "mem"),
                CompressCount = entries.Count(e => e.Type == "compress")
            };

            if (entries.Count > 0)
            {
                stats.FirstEntry = entries[0].Timestamp;
                stats.LastEntry = entries[^1].Timestamp;
            }

            return stats;
        }
    }

    // ========== Private Methods ==========

    private void LoadLastHash()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
                return;

            try
            {
                var lines = File.ReadAllLines(_filePath);
                if (lines.Length > 0)
                {
                    var lastLine = lines[^1];
                    var entry = ParseEntry(lastLine);
                    if (entry != null)
                    {
                        _lastHash = ComputeEntryHash(entry.Timestamp, entry.Type, entry.Id, entry.ContentHash, entry.PrevHash);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("加载账本最后哈希失败：", ex.Message);
            }
        }
    }

    private void RecordEntry(string type, string id, string contentHash)
    {
        lock (_gate)
        {
            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var prevHash = _lastHash;
                var line = $"{timestamp}|{type}|{id}|{contentHash}|{prevHash}";

                // 追加到文件
                File.AppendAllText(_filePath, line + Environment.NewLine);

                // 更新最后哈希
                _lastHash = ComputeEntryHash(timestamp, type, id, contentHash, prevHash);

                _log.Debug($"记录到账本：{type} {id}");
            }
            catch (Exception ex)
            {
                _log.Warn($"写入账本失败：{ex.Message}");
            }
        }
    }

    private List<LedgerEntry> ReadAllEntries()
    {
        var entries = new List<LedgerEntry>();
        
        if (!File.Exists(_filePath))
            return entries;

        var lines = File.ReadAllLines(_filePath);
        foreach (var line in lines)
        {
            var entry = ParseEntry(line);
            if (entry != null)
                entries.Add(entry);
        }

        return entries;
    }

    private LedgerEntry? ParseEntry(string line)
    {
        var parts = line.Split('|');
        if (parts.Length != 5)
            return null;

        if (!long.TryParse(parts[0], out var timestamp))
            return null;

        return new LedgerEntry
        {
            Timestamp = timestamp,
            Type = parts[1],
            Id = parts[2],
            ContentHash = parts[3],
            PrevHash = parts[4]
        };
    }

    private string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16]; // 取前16个字符
    }

    private string ComputeEntryHash(long timestamp, string type, string id, string contentHash, string prevHash)
    {
        var entry = $"{timestamp}|{type}|{id}|{contentHash}|{prevHash}";
        return ComputeContentHash(entry);
    }

    private sealed class LedgerEntry
    {
        public long Timestamp { get; set; }
        public string Type { get; set; } = "";
        public string Id { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string PrevHash { get; set; } = "";
    }
}

/// <summary>验证结果</summary>
public sealed class VerificationResult
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = "";
    public int BrokenAt { get; set; }
    public string ExpectedHash { get; set; } = "";
    public string ActualHash { get; set; } = "";
    public int TotalEntries { get; set; }
}

/// <summary>账本统计</summary>
public sealed class LedgerStats
{
    public int TotalEntries { get; set; }
    public int MessageCount { get; set; }
    public int MemoryCount { get; set; }
    public int CompressCount { get; set; }
    public long FirstEntry { get; set; }
    public long LastEntry { get; set; }
}
