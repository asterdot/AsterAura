using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Ai;
using AuraChat.Core.Ai;
using AuraChat.Core.Auth;
using AuraChat.Core.Config;
using AuraChat.Core.Logging;
using AuraChat.Core.Personas;
using AuraChat.Core.Runtime;
using AuraChat.Core.Storage;
using Xunit;

namespace AuraChat.Tests;

/// <summary>记录请求体、按固定脚本回话的假 HTTP 端点（只用来验请求组装，不联网）。</summary>
internal sealed class CaptureHandler : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"好的\"}}]}",
                Encoding.UTF8,
                "application/json"),
        };
    }
}

/// <summary>
/// 回归：`[` 开头的普通文本（人类的「引用回复」会产生 `[引用 …]\n…`）不能被当成多模态 JSON 数组解析。
/// 修之前这里会抛 JsonException（日志里就是 `'0xE5' is an invalid start of a value`），整轮对话不回复。
/// </summary>
public class LlmContentParsingTests
{
    private static (LlmClient llm, CaptureHandler handler) Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "aurachat-llm-tests-" + Guid.NewGuid().ToString("N"));
        var store = new DataStore(root);
        var log = new AppLog();
        var host = new FeatureHost
        {
            Store = store,
            Config = new ConfigStore(store, log).Load(),
            Auth = new AuthService(store, log),
            Log = log,
            Persona = new PersonaService(store, Path.Combine(root, "personas-bundled")),
        };
        var handler = new CaptureHandler();
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new LlmClient(host, http, "http://stub.invalid/v1", "sk-test"), handler);
    }

    private static JsonNode? LastContent(CaptureHandler handler, int index)
    {
        Assert.False(string.IsNullOrWhiteSpace(handler.LastBody));
        var body = JsonNode.Parse(handler.LastBody!)!;
        var messages = body["messages"]!.AsArray();
        return messages[index]!["content"];
    }

    [Fact]
    public async Task QuoteReplyText_GoesOutAsPlainString()
    {
        var (llm, handler) = Build();
        var quote = "[引用 屁股疼……我给你吹吹？ ]\n不可能，你先说的";

        var reply = await llm.AskAsync(
            new LlmOptions { Provider = "openai", Model = "gpt-4o-mini" },
            [new LlmMessage("user", quote)]);

        Assert.Equal("好的", reply);
        var content = LastContent(handler, 0);
        Assert.False(content is JsonArray, "引用文本被当成多模态数组了");
        Assert.Equal(quote, content!.GetValue<string>());
    }

    [Fact]
    public async Task MultimodalArray_StillPassedThroughAsArray()
    {
        var (llm, handler) = Build();
        const string multimodal =
            "[{\"type\":\"text\",\"text\":\"看图\"}," +
            "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAAA\"}}]";

        var reply = await llm.AskAsync(
            new LlmOptions { Provider = "openai", Model = "gpt-4o-mini" },
            [new LlmMessage("user", multimodal)]);

        Assert.Equal("好的", reply);
        var content = LastContent(handler, 0);
        var arr = Assert.IsType<JsonArray>(content);
        Assert.Equal(2, arr.Count);
        Assert.Equal("text", arr[0]!["type"]!.GetValue<string>());
    }
}
