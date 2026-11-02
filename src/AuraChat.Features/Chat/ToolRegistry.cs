using System.Text.Json.Nodes;
using AuraChat.Core.Features;
using AuraChat.Core.Logging;
using AuraChat.Core.Runtime;

namespace AuraChat.Features.Chat;

/// <summary>
/// 工具登记表（对齐原 tools/tools.js 的注册表 + 调用分发）。
/// 各功能模块自己 Register；聊天流水线只认 All() 交出来的列表。
/// 构造时先把 17 个 schema 都摆上去（模型看得到工具），
/// 真正能跑的要等模块注册执行函数——没注册的调用时回「这个工具暂时不可用」。
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    /// <summary>没有执行函数时给模型的回话（原版行为：工具还没接上）。</summary>
    public const string UnavailableMessage = "这个工具暂时不可用。";

    private readonly object _gate = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Slot> _tools = new(StringComparer.Ordinal);
    private readonly AreaLog _log;

    private sealed class Slot
    {
        public required ToolSchema Schema { get; init; }
        public ToolDef? Def { get; set; }
        public bool Ready => Def is not null;
    }

    /// <param name="host">用来挂日志、把自己放进 host.Set&lt;IToolRegistry&gt;()。</param>
    /// <param name="declareAll">true = 启动时把 17 个 schema 全登记（只是声明，还不能跑）。</param>
    public ToolRegistry(FeatureHost host, bool declareAll = true)
    {
        _log = host.Log.Child("tools");
        if (declareAll)
        {
            foreach (var schema in ToolSchemas.All) Declare(schema);
        }
        host.Set<IToolRegistry>(this);
    }

    /// <summary>只登记 schema（工具先摆出来，执行函数以后补）。重复登记以新的为准。</summary>
    public void Declare(ToolSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_gate)
        {
            if (_tools.TryGetValue(schema.Name, out var slot))
            {
                slot.Def = null;
                _tools[schema.Name] = new Slot { Schema = schema };
            }
            else
            {
                _tools[schema.Name] = new Slot { Schema = schema };
                _order.Add(schema.Name);
            }
        }
    }

    /// <summary>登记一个能跑的工具（同名会覆盖之前的声明）。</summary>
    public void Register(ToolDef tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name)) throw new ArgumentException("工具名不能为空", nameof(tool));
        lock (_gate)
        {
            if (_tools.TryGetValue(tool.Name, out var slot))
            {
                slot.Def = tool;
            }
            else
            {
                if (ToolSchemas.Get(tool.Name) is { } known)
                {
                    _tools[tool.Name] = new Slot { Schema = known, Def = tool };
                }
                else
                {
                    _tools[tool.Name] = new Slot
                    {
                        Schema = new ToolSchema(tool.Name, tool.Description, (JsonObject)tool.InputSchema.DeepClone()),
                        Def = tool,
                    };
                }
                _order.Add(tool.Name);
            }
        }
        _log.Debug("登记工具 {0}", tool.Name);
    }

    /// <summary>用现成 schema + 执行函数登记：Register(ToolSchemas.SendSticker, HandlerAsync)。</summary>
    public void Register(string name, Func<JsonObject, CancellationToken, Task<string>> run)
        => Register((ToolSchemas.Get(name) ?? throw new ArgumentException($"没有这个工具的 schema：{name}", nameof(name))).ToToolDef(run));

    public void Register(ToolSchema schema, Func<JsonObject, CancellationToken, Task<string>> run)
        => Register(schema.ToToolDef(run));

    /// <summary>批量登记（模块启动时一把挂上去）。</summary>
    public void RegisterAll(IEnumerable<ToolDef> tools)
    {
        foreach (var tool in tools) Register(tool);
    }

    /// <summary>当前工具列表（顺序 = 首次登记顺序，与原版注册顺序一致）。</summary>
    public IReadOnlyList<ToolDef> All()
    {
        lock (_gate)
        {
            var list = new List<ToolDef>(_order.Count);
            foreach (var name in _order)
            {
                var slot = _tools[name];
                list.Add(slot.Def ?? new ToolDef(slot.Schema.Name, slot.Schema.Description,
                    (JsonObject)slot.Schema.Parameters.DeepClone(), (_, _) => Task.FromResult(UnavailableMessage)));
            }
            return list;
        }
    }

    /// <summary>这个名字有没有登记过（不管是 schema 还是能跑的）。</summary>
    public bool Has(string? name)
    {
        if (name is null) return false;
        lock (_gate) return _tools.ContainsKey(name);
    }

    /// <summary>登记了且执行函数已经挂上。</summary>
    public bool IsReady(string? name)
    {
        if (name is null) return false;
        lock (_gate) return _tools.TryGetValue(name, out var slot) && slot.Ready;
    }

    public int Count
    {
        get { lock (_gate) return _tools.Count; }
    }

    /// <summary>
    /// 按名字调工具。未知工具给模型一句中文提醒；
    /// 声明了但还没接执行函数 → 「这个工具暂时不可用」；执行中的异常不往上抛，包成文字回给模型。
    /// </summary>
    public async Task<string> InvokeAsync(string name, JsonObject args, CancellationToken ct = default)
    {
        ToolDef? def;
        bool known;
        lock (_gate)
        {
            known = _tools.TryGetValue(name ?? "", out var slot);
            def = known ? slot!.Def : null;
        }

        if (!known) return $"没有「{name}」这个工具，检查一下名字是不是写错了。";
        if (def is null) return UnavailableMessage;

        try
        {
            return await def.Run(args ?? new JsonObject(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("工具 {0} 执行失败：{1}", name, ex.Message);
            return $"工具「{name}」执行出错：{ex.Message}";
        }
    }
}
