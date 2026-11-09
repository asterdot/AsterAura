using System.Text.Json.Nodes;

namespace AuraChat.Ai;

/// <summary>
/// 模型提供者接口：统一用户自带Key、官方额度中转、本地模型三种模式。
/// 每个实现负责：HTTP调用、计费、额度检查、错误处理。
/// </summary>
public interface IModelProvider
{
    /// <summary>提供者名称（用于日志和诊断）。</summary>
    string Name { get; }

    /// <summary>
    /// 非流式调用：发送请求体到端点，返回完整响应。
    /// </summary>
    /// <param name="endpoint">解析后的端点（地址/密钥/模型）</param>
    /// <param name="body">OpenAI 兼容的请求体</param>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>完整响应 JSON</returns>
    Task<JsonObject> AskAsync(Endpoint endpoint, JsonObject body, int timeoutMs, CancellationToken ct);

    /// <summary>
    /// 流式调用：发送请求体到端点，逐块返回内容。
    /// </summary>
    /// <param name="endpoint">解析后的端点</param>
    /// <param name="body">OpenAI 兼容的请求体</param>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>内容块流（仅返回 delta.content）</returns>
    IAsyncEnumerable<string> StreamAsync(Endpoint endpoint, JsonObject body, int timeoutMs, CancellationToken ct);
}
