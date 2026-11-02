using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;
using Microsoft.AspNetCore.Http;

namespace AuraChat.Server;

/// <summary>
/// SSE 广播：网页端/桌面端/手机端都通过 GET api/stream?token= 连上来，
/// 服务端用 Emit(type, payload) 推送（对齐原 server.js 的 emit，按 chatId 过滤）。
/// </summary>
public sealed class SseHub(AppLog log) : ISseEmitter, IAsyncDisposable
{
    private sealed class Client
    {
        public required HttpResponse Response { get; init; }
        public required string ChatId { get; init; }
        public required CancellationToken Token { get; init; }
    }

    private readonly List<Client> _clients = [];
    private readonly Lock _gate = new();
    private readonly AreaLog _log = log.Child("sse");
    private Timer? _heartbeat;

    public int Count
    {
        get { lock (_gate) return _clients.Count; }
    }

    public void StartHeartbeat(TimeSpan interval)
    {
        _heartbeat ??= new Timer(_ => Ping(), null, interval, interval);
    }

    private void Ping()
    {
        List<Client> snapshot;
        lock (_gate) snapshot = [.. _clients];
        foreach (var c in snapshot)
        {
            try
            {
                c.Response.WriteAsync(": ping\n\n", c.Token).GetAwaiter().GetResult();
                c.Response.Body.FlushAsync(c.Token).GetAwaiter().GetResult();
            }
            catch
            {
                Remove(c);
            }
        }
    }

    /// <summary>挂一个 SSE 连接，直到客户端断开。</summary>
    public async Task AttachAsync(HttpContext ctx, string chatId)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Connection = "keep-alive";
        ctx.Response.Headers["x-accel-buffering"] = "no";
        await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        var client = new Client { Response = ctx.Response, ChatId = chatId, Token = ctx.RequestAborted };
        lock (_gate) _clients.Add(client);

        try
        {
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Remove(client);
        }
    }

    private void Remove(Client client)
    {
        lock (_gate) _clients.Remove(client);
    }

    /// <summary>广播事件。payload.chatId 存在时只发给同一个 chatId 的连接。</summary>
    public void Emit(string type, JsonObject payload)
    {
        var body = new JsonObject { ["type"] = type };
        foreach (var (k, v) in payload) body[k] = v?.DeepClone();
        var frame = $"data: {body.ToJsonString()}\n\n";
        var chatId = payload["chatId"]?.ToString();

        List<Client> snapshot;
        lock (_gate) snapshot = [.. _clients];

        foreach (var c in snapshot)
        {
            if (!string.IsNullOrEmpty(c.ChatId) && !string.IsNullOrEmpty(chatId) && c.ChatId != chatId) continue;
            try
            {
                c.Response.WriteAsync(frame, Encoding.UTF8, c.Token).GetAwaiter().GetResult();
                c.Response.Body.FlushAsync(c.Token).GetAwaiter().GetResult();
            }
            catch
            {
                Remove(c);
            }
        }
    }

    public void Emit(string type, object payload)
        => Emit(type, JsonSerializerShim.ToJsonObject(payload));

    public ValueTask DisposeAsync()
    {
        _heartbeat?.Dispose();
        List<Client> snapshot;
        lock (_gate) snapshot = [.. _clients];
        foreach (var c in snapshot)
        {
            try { c.Response.WriteAsync(": bye\n\n").GetAwaiter().GetResult(); } catch { }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>把任意对象序列化成 JsonObject，供 SSE 用。</summary>
public static class JsonSerializerShim
{
    public static JsonObject ToJsonObject(object payload)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }
}
