using System.Net;
using AuraChat.Core.Ai;
using AuraChat.Core.Runtime;

namespace AuraChat.Ai;

/// <summary>
/// AI 层的装配入口：建一个共享 HttpClient，把 LlmClient / ModelCatalog 挂到 FeatureHost 上，
/// 其它模块（聊天、记忆、心情、日记、guard…）一律通过 host.Require&lt;ILlmClient&gt;() 取。
/// </summary>
public static class AiModule
{
    private static readonly Lock Gate = new();
    private static HttpClient? _shared;

    /// <summary>装配 AI 层并返回两个实现（AppHost 启动时调一次）。</summary>
    public static (ILlmClient Llm, IModelCatalog Catalog) Create(FeatureHost host)
    {
        var http = SharedHttpClient();
        
        // 根据配置选择 Provider
        var providerMode = host.Str("provider", "mode");
        var quotaServiceUrl = host.Str("provider", "quotaServiceUrl");
        
        IModelProvider provider = providerMode.ToLowerInvariant() switch
        {
            "local" => new LocalModelProvider(http),
            "auraquota" or "quota" when !string.IsNullOrWhiteSpace(quotaServiceUrl) 
                => new AuraQuotaProvider(http, quotaServiceUrl, () => GetAccessToken(host)),
            _ => new UserKeyProvider(http) // 默认用户Key模式
        };
        
        var llm = new LlmClient(host, http, null, null, provider);
        var catalog = new ModelCatalog(host, http);

        host.Set<ILlmClient>(llm);
        host.Set<IModelCatalog>(catalog);
        host.Set<IModelProvider>(provider); // 注册 Provider 实例

        var providerName = host.Str("model", "provider");
        var model = host.Str("model", "model");
        var baseUrl = host.Str("model", "baseUrl");
        var hasKey = !string.IsNullOrWhiteSpace(host.Str("model", "apiKey"));

        host.Log.Child("ai").Info(
            "模型层已装配：" +
            $"provider={(providerName.Length == 0 ? "（未选）" : providerName)}，" +
            $"model={(model.Length == 0 ? "默认" : model)}，" +
            $"接口={(baseUrl.Length == 0 ? "预设" : baseUrl)}，" +
            $"密钥={(hasKey ? "已配置" : "没填")}，" +
            $"模式={provider.Name}");

        return (llm, catalog);
    }
    
    /// <summary>获取当前用户访问令牌（用于 AuraQuota 模式）。</summary>
    private static string? GetAccessToken(FeatureHost host)
    {
        // TODO: 从当前会话或认证服务获取访问令牌
        // 当前版本返回配置中的静态令牌作为占位
        return host.Str("provider", "accessToken");
    }

    /// <summary>
    /// 给用独立端点的模块（guard 自带 baseUrl/apiKey）造一个客户端。
    /// 不传 http 时自己新建一个（用完由调用方的生命周期决定，通常和进程同寿）。
    /// </summary>
    public static LlmClient CreatePinned(FeatureHost host, string baseUrl, string apiKey, HttpClient? http = null)
        => new(host, http ?? NewHttpClient(), baseUrl, apiKey);

    /// <summary>进程内共享的 HttpClient（超时按每次调用自己控制，这里设成无限）。</summary>
    public static HttpClient SharedHttpClient()
    {
        if (_shared is not null) return _shared;
        lock (Gate)
        {
            return _shared ??= NewHttpClient();
        }
    }

    private static HttpClient NewHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };

        var http = new HttpClient(handler, disposeHandler: true)
        {
            // 单次调用的超时由 LlmOptions.TimeoutMs / CTS 控制，避免流式长回复被 HttpClient 提前砍断
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AuraChat/2.0");
        return http;
    }
}
