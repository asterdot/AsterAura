using System.Text.Json.Nodes;
using AuraChat.Core.Ai;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;

namespace AuraChat.Features.Chat;

/// <summary>
/// 最小工具调用循环（OpenAI 兼容协议）：带 tools 请求 → 模型回 tool_calls → 执行 → 结果回填 → 再问 → 得最终文本。
/// 最多 MaxRounds 轮，防止模型无限连环调工具。任何一轮失败都回落成"已有文本/空串"，不让聊天挂掉。
/// </summary>
public sealed class ToolLoop(ILlmClient llm, IToolRegistry tools, AreaLog log)
{
    /// <summary>单轮对话最多几次工具往返。</summary>
    public const int MaxRounds = 4;

    public async Task<string> RunAsync(
        LlmOptions options,
        IReadOnlyList<LlmMessage> messages,
        CancellationToken ct = default)
    {
        var defs = tools.All();
        if (defs.Count == 0)
            return await llm.AskAsync(options, messages, ct).ConfigureAwait(false);

        var toolsArr = new JsonArray();
        foreach (var def in defs)
        {
            toolsArr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = def.Name,
                    ["description"] = def.Description,
                    ["parameters"] = def.InputSchema.DeepClone(),
                },
            });
        }

        var history = new JsonArray();
        string lastText = "";

        for (var round = 0; round < MaxRounds; round++)
        {
            JsonObject msg;
            try
            {
                msg = await llm.AskMessageAsync(options, messages, toolsArr, history.Count > 0 ? history : null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 多模态/工具协议模型不支持时会在这里炸：回落成不带工具的纯文本调用
                log.Warn("工具轮调用失败，回落纯文本：", ex.Message);
                return history.Count > 0 ? lastText : await llm.AskAsync(options, messages, ct).ConfigureAwait(false);
            }

            var calls = msg["tool_calls"] as JsonArray;
            var content = ExtractText(msg);
            if (content.Length > 0) lastText = content;

            if (calls is null || calls.Count == 0)
                return lastText;

            // 记住 assistant 这条（含 tool_calls），回填执行结果
            history.Add(msg.DeepClone());
            foreach (var call in calls)
            {
                if (call is not JsonObject co) continue;
                var fn = co["function"] as JsonObject;
                var name = fn?["name"]?.ToString() ?? "";
                JsonObject args;
                try
                {
                    args = (JsonNode.Parse(fn?["arguments"]?.ToString() ?? "{}") as JsonObject) ?? new JsonObject();
                }
                catch
                {
                    args = new JsonObject();
                }

                var result = await tools.InvokeAsync(name, args, ct).ConfigureAwait(false);
                log.Debug($"工具 {name} → {(result.Length > 120 ? result[..120] : result)}");
                history.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = co["id"]?.ToString() ?? "",
                    ["content"] = result,
                });
            }
        }

        // 轮次用完：让她用已有信息作答
        history.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "（工具轮次用完了，用你已经拿到的信息直接回答。）",
        });
        try
        {
            var final = await llm.AskMessageAsync(options, messages, null, history, ct).ConfigureAwait(false);
            var text = ExtractText(final);
            return text.Length > 0 ? text : lastText;
        }
        catch
        {
            return lastText;
        }
    }

    private static string ExtractText(JsonObject msg)
    {
        var content = msg["content"];
        if (content is null) return "";
        if (content is JsonValue) return content.ToString();
        // 多模态 content 数组：拼所有 text part
        if (content is JsonArray arr)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in arr)
            {
                if (part is JsonObject po && po["type"]?.ToString() == "text")
                    sb.Append(po["text"]?.ToString());
            }
            return sb.ToString();
        }
        return "";
    }
}
