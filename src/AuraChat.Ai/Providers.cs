using System.Text.Json.Nodes;
using AuraChat.Core;
using AuraChat.Core.Config;

namespace AuraChat.Ai;

/// <summary>
/// 一家模型服务的预设值：设置页的 provider 下拉、默认接口地址、默认模型清单都从这里取。
/// 这样对方不实现 GET /models、或者本机还没网时，下拉框里也永远有东西可选。
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string BaseUrl,
    string DefaultModel,
    bool NeedsKey,
    string[] Models);

/// <summary>provider 预设表（全部走 OpenAI 兼容协议）。</summary>
public static class Providers
{
    /// <summary>自定义（OpenAI 兼容）：接口地址完全由配置里的 model.baseUrl 决定，空 key 时不发 Authorization。</summary>
    public const string CustomId = "custom";

    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", true,
            ["gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "o3-mini", "o4-mini"]),

        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", true,
            ["deepseek-chat", "deepseek-reasoner"]),

        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", true,
            ["glm-4-plus", "glm-4-air", "glm-4-flash", "glm-4-long", "glm-z1-air"]),

        new("moonshot", "Moonshot（Kimi）", "https://api.moonshot.cn/v1", "moonshot-v1-8k", true,
            ["moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k", "kimi-latest"]),

        new("qwen", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", true,
            ["qwen-max", "qwen-plus", "qwen-turbo", "qwen-long"]),

        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct", true,
            ["deepseek-ai/DeepSeek-V3", "deepseek-ai/DeepSeek-R1", "Qwen/Qwen2.5-72B-Instruct",
             "Qwen/Qwen2.5-7B-Instruct", "meta-llama/Llama-3.1-8B-Instruct"]),

        new(CustomId, "自定义（OpenAI 兼容）", "", "", false, []),
    ];

    /// <summary>常见别名 → 预设 id，前端和老配置里怎么写都能对上。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt"] = "openai",
        ["open-ai"] = "openai",
        ["deep-seek"] = "deepseek",
        ["glm"] = "zhipu",
        ["bigmodel"] = "zhipu",
        ["zhipuai"] = "zhipu",
        ["kimi"] = "moonshot",
        ["moonshot-ai"] = "moonshot",
        ["dashscope"] = "qwen",
        ["aliyun"] = "qwen",
        ["tongyi"] = "qwen",
        ["sf"] = "siliconflow",
        ["silicon"] = "siliconflow",
        ["openai-compatible"] = CustomId,
        ["local"] = CustomId,
    };

    public static ProviderPreset? Find(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0) return null;
        foreach (var preset in All)
            if (string.Equals(preset.Id, key, StringComparison.OrdinalIgnoreCase)) return preset;
        return Aliases.TryGetValue(key, out var target) ? Find(target) : null;
    }

    /// <summary>认不出来的 id 也原样返回（用配置里的 baseUrl 当自定义服务），不会抛异常。</summary>
    public static ProviderPreset Get(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0) key = CustomId;
        return Find(key) ?? new ProviderPreset(key, key, "", "", true, []);
    }

    /// <summary>把别名归一成预设 id；空串和认不出的返回原值（小写去空白）。</summary>
    public static string Normalize(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0) return CustomId;
        var hit = Find(key);
        return hit?.Id ?? key.ToLowerInvariant();
    }

    /// <summary>拼接口路径：base 去掉尾斜杠，path 去掉头斜杠。</summary>
    public static string Join(string baseUrl, string path)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        var p = (path ?? "").Trim().TrimStart('/');
        if (b.Length == 0) return "";
        return p.Length == 0 ? b : b + "/" + p;
    }

    /// <summary>拿不到远端 /models 时用的静态清单（{provider, model, label}）。</summary>
    public static JsonArray StaticCatalog()
    {
        var arr = new JsonArray();
        foreach (var preset in All)
        {
            foreach (var model in preset.Models)
            {
                arr.Add(new JsonObject
                {
                    ["provider"] = preset.Id,
                    ["model"] = model,
                    ["label"] = preset.Name + " / " + model,
                });
            }
        }
        return arr;
    }

    /// <summary>给设置页下拉用的完整预设表。</summary>
    public static JsonArray ToJson()
    {
        var arr = new JsonArray();
        foreach (var preset in All)
        {
            var models = new JsonArray();
            foreach (var model in preset.Models) models.Add(model);
            arr.Add(new JsonObject
            {
                ["id"] = preset.Id,
                ["name"] = preset.Name,
                ["baseUrl"] = preset.BaseUrl,
                ["defaultModel"] = preset.DefaultModel,
                ["needsKey"] = preset.NeedsKey,
                ["models"] = models,
            });
        }
        return arr;
    }
}

/// <summary>一次调用最终用到的地址 / 密钥 / 模型（provider 预设 + 配置合并后的结果）。</summary>
internal sealed record Endpoint(
    string ProviderId,
    string ProviderName,
    string BaseUrl,
    string ApiKey,
    string Model,
    bool NeedsKey,
    bool ThinkingOff = false)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>拼出完整接口地址，例如 {base}/chat/completions。</summary>
    public string Url(string path) => Providers.Join(BaseUrl, path);

    /// <summary>调用参数里的 model 优先，空则用配置/预设的默认模型。</summary>
    public string ModelOr(string? requested)
        => string.IsNullOrWhiteSpace(requested) ? Model : requested!.Trim();
}

/// <summary>把「配置的 model 段 + 预设 + 调用参数」合成实际请求参数。</summary>
internal static class EndpointResolver
{
    /// <param name="baseUrlOverride">固定接口地址（guard 这类用独立端点的模块），优先级最高。</param>
    /// <param name="apiKeyOverride">固定密钥，优先级最高。</param>
    public static Endpoint Resolve(
        ConfigStore config,
        string? provider,
        string? model,
        string? baseUrlOverride = null,
        string? apiKeyOverride = null)
    {
        var section = config.Current["model"] as JsonObject;

        var wanted = FirstNonEmpty(provider, section?["provider"]?.ToString());
        var known = Providers.Find(wanted);
        var id = known?.Id ?? Providers.Normalize(wanted);
        var preset = known ?? Providers.Get(id);

        // 认不出的 provider id：配置里如果有同名段落（guard 就是自带 baseUrl / apiKey / model 的段落），
        // 就当成这个段落自己的端点 —— 原版 guard.js 只读 cfg.guard，不看 model 段。
        var own = known is null && !string.IsNullOrWhiteSpace(wanted)
            ? config.Current[wanted!.Trim()] as JsonObject ?? config.Current[id] as JsonObject
            : null;

        var baseUrl = FirstNonEmpty(baseUrlOverride, own?["baseUrl"]?.ToString(), section?["baseUrl"]?.ToString(), preset.BaseUrl);
        var apiKey = FirstNonEmpty(apiKeyOverride, own?["apiKey"]?.ToString(), section?["apiKey"]?.ToString());
        var modelId = FirstNonEmpty(model, own?["model"]?.ToString(), section?["model"]?.ToString(), preset.DefaultModel);

        return new Endpoint(
            preset.Id,
            preset.Name,
            (baseUrl ?? "").Trim().TrimEnd('/'),
            (apiKey ?? "").Trim(),
            modelId ?? "",
            // 独立段落（guard）通常是本机小模型，没 key 也能连，不要报「还没填密钥」
            preset.NeedsKey && own is null,
            // guard 那种小模型默认开思维链的话，maxTokens=1 会全烧在 reasoning 上，content 是空的
            own is not null);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }
}
