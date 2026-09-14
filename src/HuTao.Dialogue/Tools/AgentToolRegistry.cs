using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Tools;

/// <summary>
/// 工具注册表：把「这一轮该装配哪些工具」从工厂代码里挪出来。
///
/// 背景：`AgentRuntimeFactory.CreateTools` 里原本是一串 `new XxxTool(...)`，
/// 于是「某个角色需要哪个工具」这件事只能改代码。现在改成：
/// 注册表登记**名字 → 构造器**，角色包用名字声明需要哪些工具。
///
/// 加一个新工具 = 注册一次；让某个角色用上它 = 改配置。两者互不影响。
///
/// 注意这里刻意**不做依赖注入容器**——只有十来个工具、单进程、构造顺序明确，
/// 引入容器带来的间接层远大于收益。构造器直接接收它需要的东西即可。
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, Func<IAgentTool?>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    /// <summary>登记一个工具构造器。同一名字重复登记时后来者覆盖（便于宿主覆盖内置实现）。</summary>
    public AgentToolRegistry Register(string name, Func<IAgentTool?> factory)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("工具名不能为空。", nameof(name));
        if (!_factories.ContainsKey(name))
            _order.Add(name);
        _factories[name] = factory;
        return this;
    }

    /// <summary>已登记的工具名（按登记顺序）。</summary>
    public IReadOnlyList<string> RegisteredNames => _order;

    /// <summary>
    /// 按名字装配工具。**没登记的、或构造器返回 null 的（例如依赖缺失）都安静跳过**——
    /// 少一个工具只该让能力变弱，不该让角色起不来。
    /// </summary>
    public IReadOnlyList<IAgentTool> Resolve(IEnumerable<string>? names, Action<string>? log = null)
    {
        var result = new List<IAgentTool>();
        var requested = names?.ToList() ?? _order;
        foreach (var name in requested)
        {
            if (!_factories.TryGetValue(name, out var factory))
            {
                log?.Invoke($"[tools] 未登记的工具，已跳过：{name}");
                continue;
            }
            try
            {
                var tool = factory();
                if (tool is not null)
                    result.Add(tool);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[tools] 工具 {name} 装配失败，已跳过：{ex.GetType().Name}");
            }
        }
        return result;
    }

    /// <summary>
    /// 把外部来源（MCP / 远程工具服务）的工具并进来。
    /// 这些工具的名字在编译期未知，所以只能整体注册成一个「集合」。
    /// </summary>
    public AgentToolRegistry RegisterExternalSource(IExternalToolSource source)
        => Register(source.SourceName, () => new ExternalToolGroup(source));
}

/// <summary>
/// 一个外部来源的全部工具，作为一个整体参与装配。
/// 之所以包一层而不是逐个注册：外部来源的工具名在**运行期**才知道，
/// 而注册表是静态的；包一层可以让「启用/停用整个 MCP 服务」成为一次开关。
/// </summary>
public sealed class ExternalToolGroup(IExternalToolSource source) : IAgentTool
{
    public IExternalToolSource Source { get; } = source;

    public string Name => Source.SourceName;
    public string Description => $"{Source.SourceName} 提供的工具组（{string.Join('、', LastKnownNames)}）";
    public AgentToolPolicy Policy => new(AgentToolEffect.ExternalMutation,
        AgentToolSensitivity.None, RequiresExplicitIntent: true, CanRunInParallel: false);

    private IReadOnlyList<string> LastKnownNames { get; set; } = [];

    /// <summary>展开成一个个独立工具（提示词里逐个列出，模型才能按名字选）。</summary>
    public async Task<IReadOnlyList<IAgentTool>> ExpandAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var tools = await McpToolAdapter.WrapAsync(Source, log: log, ct: ct).ConfigureAwait(false);
        LastKnownNames = tools.Select(t => t.Name).ToArray();
        return tools;
    }

    /// <summary>不展开时它自己不是一个可调用的工具——必须先用 <see cref="ExpandAsync"/>。</summary>
    public Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
        => Task.FromResult($"{Source.SourceName} 是工具组，请调用其中具体的工具。");
}
