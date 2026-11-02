using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace AuraChat.Server;

/// <summary>一次 API 调用的上下文。</summary>
public sealed class ApiCall(HttpContext http, ApiContext api, string path)
{
    public HttpContext Http { get; } = http;
    public ApiContext Api { get; } = api;
    /// <summary>去掉 /aurachat/ 前缀后的路径，例如 api/state。</summary>
    public string Path { get; } = path;
    public HttpRequest Request => Http.Request;
    public IQueryCollection Query => Http.Request.Query;
}

/// <summary>极简路由表：先注册先匹配，精确优先于前缀（对齐 web/router.js）。</summary>
public sealed class Router
{
    private readonly List<(string Method, string Path, bool Exact, Func<ApiCall, Task> Handler)> _table = [];

    public void Add(string method, string path, Func<ApiCall, Task> handler)
        => _table.Add((method.ToUpperInvariant(), path, true, handler));

    public void Prefix(string method, string path, Func<ApiCall, Task> handler)
        => _table.Add((method.ToUpperInvariant(), path, false, handler));

    public int Size => _table.Count;

    public Func<ApiCall, Task>? Match(string method, string path)
    {
        method = method.ToUpperInvariant();
        foreach (var (m, p, exact, handler) in _table)
        {
            if (m != method) continue;
            if (exact ? path == p : path.StartsWith(p, StringComparison.Ordinal)) return handler;
        }
        return null;
    }
}

/// <summary>
/// 路由模块标记。实现方式：public static 类里写 `public static void Register(Router r, ApiContext api)`，
/// ServerHost 启动时反射扫描带 [RouteModule] 的类按 Order 顺序注册 —— 新增模块只加文件，不改公共代码。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteModuleAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

public static class RouteLoader
{
    public static void LoadAll(Router router, ApiContext api)
    {
        var modules = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetCustomAttribute<RouteModuleAttribute>() is not null)
            .OrderBy(t => t.GetCustomAttribute<RouteModuleAttribute>()!.Order)
            .ToList();

        foreach (var type in modules)
        {
            var method = type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
            if (method is null) continue;
            method.Invoke(null, [router, api]);
        }
    }
}
