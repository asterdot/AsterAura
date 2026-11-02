using System.Text.Json.Nodes;

namespace AuraChat.Core.Runtime;

/// <summary>SSE 广播口子（原 server.js 的 emit）。由 Server 层的 SseHub 实现。</summary>
public interface ISseEmitter
{
    void Emit(string type, JsonObject payload);
    void Emit(string type, object payload);
}

/// <summary>状态面板里的一块（心情 / 日记 / 记忆…），由各功能模块实现并注册。</summary>
public interface IStatusProvider
{
    string Key { get; }
    JsonNode? Snapshot();
}

/// <summary>自驱备忘（api/selfdrive）。</summary>
public interface ISelfDriveMemo
{
    string ReadMemo();
    JsonObject State();
    JsonObject WriteMemo(string? text);
}

/// <summary>冲浪兴趣库（api/surf）。</summary>
public interface ISurfModule
{
    JsonArray ListInterests();
    JsonObject AddInterest(string? topic, string? why);
    JsonObject NoteInterest(string? topic, string? note);
}

/// <summary>可供「测试」按钮触发的动作（api/test）：speakup / diary / selfdrive / social。</summary>
public interface ITestAction
{
    string Name { get; }
    Task<JsonObject> RunAsync();
}

/// <summary>往 system prompt 里加一段上下文（心情行 / 缺口池 / 技能 / 记忆等）。</summary>
public interface IContextLines
{
    IReadOnlyList<string> ContextLines();
}
