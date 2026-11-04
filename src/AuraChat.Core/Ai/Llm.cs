using System.Text.Json.Nodes;

namespace AuraChat.Core.Ai;

/// <summary>一条发给模型的对话消息。role = system / user / assistant。</summary>
public sealed record LlmMessage(string Role, string Content);

/// <summary>一次模型调用的参数（对齐原 core/llm.js 的 askModel 选项）。</summary>
public sealed class LlmOptions
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string? System { get; init; }
    public double Temperature { get; init; } = 1.0;
    public int MaxTokens { get; init; } = 1024;
    public string ReasoningEffort { get; init; } = "low";
    public int TimeoutMs { get; init; } = 120_000;

    /// <summary>要 top_logprobs 的个数（只有 guard 自查用；0 = 不要）。</summary>
    public int TopLogprobs { get; init; }
}

/// <summary>带逐 token logprob 的返回（guard.js 用：logprob(旁) - logprob(说) &gt;= threshold 就删）。</summary>
public sealed record LlmLogprobResult(string Text, List<TokenLogprob> Tokens);

public sealed record TokenLogprob(string Token, double Logprob, Dictionary<string, double> Top);

/// <summary>模型调用口子。独立版由 AuraChat.Ai 的 LlmClient 实现（OpenAI 兼容协议）。</summary>
public interface ILlmClient
{
    Task<string> AskAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default);

    /// <summary>非流式调用，返回原始 message 对象（可能含 tool_calls）。工具循环用；tools/toolHistory 可空。</summary>
    Task<JsonObject> AskMessageAsync(
        LlmOptions options,
        IReadOnlyList<LlmMessage> messages,
        JsonArray? tools = null,
        JsonArray? toolHistory = null,
        CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default);

    Task<LlmLogprobResult> AskWithLogprobsAsync(LlmOptions options, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default);
}

/// <summary>模型目录：软件里下拉框能选的 provider / 模型，以及当前默认选择。</summary>
public interface IModelCatalog
{
    /// <summary>GET api/models 的返回（原版依赖 DSH 宿主；独立版自己列）。</summary>
    Task<JsonArray> ListModelsAsync(CancellationToken ct = default);

    /// <summary>当前配置选中的模型 {provider, model, reasoningEffort}。</summary>
    JsonObject DefaultModel();

    /// <summary>把配置里的 provider 解析成 {baseUrl, apiKey}（配完设置页要能测连通性）。</summary>
    Task<JsonObject> TestAsync(string? provider, string? model, CancellationToken ct = default);
}
