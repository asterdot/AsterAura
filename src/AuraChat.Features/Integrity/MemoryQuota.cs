using System.Text.Json.Nodes;
using AuraChat.Core.Logging;

namespace AuraChat.Features.Integrity;

/// <summary>
/// 记忆召回配额管理器：确保记忆注入不超过硬上限（3-5条）。
/// 原则：记忆可以让她想起更多，但不能让记忆淹没人格。
/// </summary>
public sealed class MemoryQuota
{
    /// <summary>默认硬上限：最多3条记忆注入</summary>
    public const int DefaultHardLimit = 3;
    
    /// <summary>绝对上限：不管配置如何，最多5条</summary>
    public const int AbsoluteMaxLimit = 5;

    private readonly AreaLog _log;
    private readonly int _hardLimit;

    public MemoryQuota(AreaLog log, int configuredLimit = DefaultHardLimit)
    {
        _log = log;
        // 确保不超过绝对上限
        _hardLimit = Math.Min(Math.Max(configuredLimit, 0), AbsoluteMaxLimit);
        
        if (_hardLimit != configuredLimit)
        {
            _log.Warn($"记忆召回配额被限制：请求 {configuredLimit}，实际 {_hardLimit}（绝对上限 {AbsoluteMaxLimit}）");
        }
    }

    /// <summary>当前配额上限</summary>
    public int HardLimit => _hardLimit;

    /// <summary>
    /// 应用配额限制：按权重排序，只保留前N条
    /// </summary>
    public List<JsonObject> ApplyQuota(List<JsonObject> memories, string reason = "")
    {
        if (memories.Count <= _hardLimit)
            return memories;

        // 超出配额，截断并记录日志
        var truncated = memories.Take(_hardLimit).ToList();
        
        _log.Warn($"记忆召回超出配额：{memories.Count} 条 → 截断为 {_hardLimit} 条" +
                  (string.IsNullOrEmpty(reason) ? "" : $"（{reason}）"));

        // 记录被截断的记忆ID（用于审计）
        var droppedIds = memories.Skip(_hardLimit)
            .Select(m => m["id"]?.ToString() ?? "unknown")
            .Take(5); // 只记录前5个
        
        _log.Debug($"被截断的记忆：{string.Join(", ", droppedIds)}...");

        return truncated;
    }

    /// <summary>
    /// 创建召回日志条目（用于审计）
    /// </summary>
    public JsonObject CreateRecallLog(
        List<JsonObject> originalMemories,
        List<JsonObject> appliedMemories,
        string query,
        long timestamp)
    {
        var log = new JsonObject
        {
            ["timestamp"] = timestamp,
            ["query"] = query,
            ["totalCandidates"] = originalMemories.Count,
            ["appliedCount"] = appliedMemories.Count,
            ["truncated"] = originalMemories.Count > appliedMemories.Count,
            ["quotaLimit"] = _hardLimit
        };

        if (log["truncated"]?.GetValue<bool>() == true)
        {
            log["droppedCount"] = originalMemories.Count - appliedMemories.Count;
        }

        // 记录应用的记忆ID（前3个）
        var appliedIds = appliedMemories
            .Take(3)
            .Select(m => m["id"]?.ToString() ?? "")
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        if (appliedIds.Count > 0)
        {
            log["appliedIds"] = new JsonArray(appliedIds.Select(id => JsonValue.Create(id)).ToArray());
        }

        return log;
    }

    /// <summary>
    /// 验证配额设置的合理性
    /// </summary>
    public QuotaValidation Validate()
    {
        var validation = new QuotaValidation
        {
            IsValid = true,
            ConfiguredLimit = _hardLimit,
            AbsoluteMax = AbsoluteMaxLimit
        };

        if (_hardLimit == 0)
        {
            validation.Warnings.Add("记忆召回配额为0，记忆系统实际上已禁用");
        }
        else if (_hardLimit == 1)
        {
            validation.Warnings.Add("记忆召回配额为1，可能影响上下文连贯性");
        }
        else if (_hardLimit >= 5)
        {
            validation.Warnings.Add("记忆召回配额达到上限，可能稀释人格");
        }

        return validation;
    }
}

/// <summary>配额验证结果</summary>
public sealed class QuotaValidation
{
    public bool IsValid { get; set; }
    public int ConfiguredLimit { get; set; }
    public int AbsoluteMax { get; set; }
    public List<string> Warnings { get; set; } = new();
}
