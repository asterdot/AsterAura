using System.Net;
using System.Text.Json.Nodes;
using AuraChat.Server;
using Xunit;

namespace AuraChat.Tests;

/// <summary>
/// API 错误响应状态码契约（t6）：
/// - 未知路由 → 404（task-4 引入）
/// - 已注册接口内部抛异常 → 5xx，且响应体 ok==false
/// - 处理器若已自行写过 401/403/404 等状态码，异常分支不得把它改写成 500
/// 全部使用进程内 ServerHost + 临时合成目录，绝不触碰真实用户数据。
/// </summary>
public class ApiErrorStatusTests : IClassFixture<ApiProtocolE2eTests.TestServer>
{
    private readonly ApiProtocolE2eTests.TestServer _fx;

    public ApiErrorStatusTests(ApiProtocolE2eTests.TestServer fx) => _fx = fx;

    private HttpClient NewClient() => new() { BaseAddress = new Uri(_fx.BaseUrl + "/") };

    [Fact]
    public async Task RegisteredRoute_ThrowsInternally_Returns500_AndErrorBody()
    {
        // 注册一条必然抛异常的接口：接口存在，但内部炸了
        _fx.Host.Router.Add("GET", "api/__boom__", _ => throw new InvalidOperationException("boom"));

        using var client = NewClient();
        var r = await client.GetAsync("api/__boom__");

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var body = JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
        Assert.NotNull(body);
        Assert.False(body!["ok"]!.GetValue<bool>());
        Assert.Contains("boom", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task HandlerAlreadySetStatus_ThenThrows_KeepsOriginalStatus()
    {
        // 处理器自己写过 401 再抛异常 —— 状态码必须保持 401，不能被异常分支改写成 500
        _fx.Host.Router.Add("GET", "api/__boom401__", call =>
        {
            call.Http.Response.StatusCode = 401;
            throw new InvalidOperationException("boom-401");
        });

        using var client = NewClient();
        var r = await client.GetAsync("api/__boom401__");

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var body = JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
        Assert.NotNull(body);
        Assert.False(body!["ok"]!.GetValue<bool>());
    }
}
