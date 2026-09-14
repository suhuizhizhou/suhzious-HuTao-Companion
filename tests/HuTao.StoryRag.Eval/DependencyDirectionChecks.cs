using System.Text.RegularExpressions;

/// <summary>
/// 依赖方向守卫：**模块之间的引用只能向下**。
///
/// 为什么需要这条用例：单个程序集内的命名空间可以随便互相引用，编译器不管。
/// 实测踩过——`Tts → Runtime`（TTS 引擎要用 Python 启动器）与
/// `Runtime → Tts`（工厂要造引擎）同时存在，环藏在同一个 assembly 里**完全隐形**，
/// 直到你试图把它们拆成两个程序集，才会突然变成一堆编译错误，
/// 而那时已经很难判断该动哪一边。
///
/// 所以把它固化成用例：**现在**就检查，而不是等到拆分那天。
///
/// 它同时兼容两种布局，因此可以在物理拆分前后一直用：
/// - 拆分前：命名空间是 <c>HuTao.Knowledge.Rag</c>，按命名空间判模块；
/// - 拆分后：命名空间是 <c>HuTao.Knowledge.Rag</c>，按前缀判模块。
/// 另外只要 <c>src/</c> 下出现多个 csproj，就**额外**检查 ProjectReference 方向——
/// 那是更强的约束（编译期就挡住了）。
/// </summary>
internal static class DependencyDirectionChecks
{
    /// <summary>模块分层：数字越小越底层。</summary>
    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal)
    {
        ["Foundation"] = 0,
        ["Bridge"] = 1,
        ["Voice"] = 2,
        ["Persona"] = 2,
        ["Knowledge"] = 3,
        ["Dialogue"] = 4,
        ["Hosts"] = 5,
    };

    /// <summary>旧命名空间（拆分前）→ 模块。</summary>
    private static readonly Dictionary<string, string> LegacyNamespaces = new(StringComparer.Ordinal)
    {
        ["HuTao.Foundation.Abstractions"] = "Foundation",
        ["HuTao.Foundation.Diagnostics"] = "Foundation",
        ["HuTao.Voice"] = "Voice",
        ["HuTao.Persona"] = "Persona",
        ["HuTao.Persona"] = "Persona",
        ["HuTao.Knowledge.Rag"] = "Knowledge",
        ["HuTao.Knowledge.Memory"] = "Knowledge",
        ["HuTao.Knowledge.DocumentReading"] = "Knowledge",
        ["HuTao.Dialogue.Core"] = "Dialogue",
        ["HuTao.Dialogue.Immersion"] = "Dialogue",
        ["HuTao.Dialogue.ChatRoom"] = "Dialogue",
        ["HuTao.Dialogue.Tools"] = "Dialogue",
        ["HuTao.Dialogue.Storage"] = "Dialogue",
        ["HuTao.Dialogue.Llm"] = "Dialogue",
    };

    /// <summary>
    /// 旧 <c>HuTao.Dialogue.Runtime</c> 是个混装命名空间（一个文件一个模块），
    /// 所以按**文件名**判。拆分后这些文件会各自落到自己的模块里，这段映射也就自然退休。
    /// </summary>
    private static readonly Dictionary<string, string> RuntimeFiles = new(StringComparer.Ordinal)
    {
        ["ProjectEnvironment"] = "Foundation",
        ["PortablePythonRuntime"] = "Bridge",
        ["TtsRuntimeFactory"] = "Voice",
        ["AgentRuntime"] = "Dialogue",
        ["AgentRuntimeFactory"] = "Dialogue",
    };

    public static List<CheckResult> Run(string repoRoot)
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool? ok, string detail = "") => checks.Add(CheckResult.Of(name, ok, detail));

        // 只认拆分后的 src/。以前这里还兜底 agent/src，那会让「布局被改坏」静默变绿，
        // 所以兜底已删除：找不到源码目录就必须红。
        var sourceRoots = new[] { Path.Combine(repoRoot, "src") }
            .Where(Directory.Exists).ToArray();
        if (sourceRoots.Length == 0)
        {
            Check("dependency_direction_acyclic", false,
                $"找不到 C# 源码目录：{Path.Combine(repoRoot, "src")}");
            return checks;
        }

        var files = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

        var violations = new List<string>();
        var known = new Dictionary<string, string>(StringComparer.Ordinal);   // 命名空间 → 模块
        var modulesSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var nsMatch = Regex.Match(text, @"(?m)^namespace ([\w\.]+);");
            if (!nsMatch.Success)
                continue;
            var ns = nsMatch.Groups[1].Value;
            var module = ResolveModule(ns, Path.GetFileNameWithoutExtension(file));
            if (module is null)
                continue;
            known[ns] = module;
            modulesSeen.Add(module);

            foreach (Match use in Regex.Matches(text, @"(?m)^using (HuTao[\w\.]*);"))
            {
                var dep = ResolveModule(use.Groups[1].Value, baseName: null)
                          ?? (known.TryGetValue(use.Groups[1].Value, out var m) ? m : null);
                if (dep is null || dep == module)
                    continue;
                if (Rank[dep] >= Rank[module])
                    violations.Add($"{module}(r{Rank[module]}) → {dep}(r{Rank[dep]})：{Path.GetFileName(file)} using {use.Groups[1].Value}");
            }
        }

        Check("dependency_direction_acyclic", violations.Count == 0,
            violations.Count == 0
                ? $"跨模块引用全部向下（模块 {modulesSeen.Count} 个，源文件 {files.Length} 个）"
                : "存在反向/同级跨模块依赖：" + string.Join("；", violations.Distinct().Take(6)));

        checks.AddRange(CheckProjectReferences(repoRoot));
        return checks;
    }

    /// <summary>
    /// 拆分之后更强的检查：csproj 的 ProjectReference 也只能指向更底层的模块。
    /// 本仓库已经拆成多项目，所以模块目录缺失不再是「跳过」，而是红。
    /// </summary>
    private static List<CheckResult> CheckProjectReferences(string repoRoot)
    {
        var checks = new List<CheckResult>();
        var srcRoot = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(srcRoot))
        {
            checks.Add(new CheckResult("dependency_project_references_acyclic", false,
                $"找不到模块目录：{srcRoot}（本仓库已拆分为多项目，缺目录视为红）"));
            return checks;
        }

        var projects = Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "hosts"), "*.csproj", SearchOption.AllDirectories)
                is var hosts && Directory.Exists(Path.Combine(repoRoot, "hosts")) ? hosts : [])
            .ToArray();

        var violations = new List<string>();
        foreach (var project in projects)
        {
            var module = ResolveModuleFromProjectPath(project);
            if (module is null)
                continue;
            var text = File.ReadAllText(project);
            foreach (Match reference in Regex.Matches(text, @"ProjectReference\s+Include=""([^""]+)"""))
            {
                var referenced = ResolveModuleFromProjectPath(
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference.Groups[1].Value)));
                if (referenced is null || referenced == module)
                    continue;
                if (Rank[referenced] >= Rank[module])
                    violations.Add($"{module} → {referenced}：{Path.GetFileName(project)}");
            }
        }

        checks.Add(new("dependency_project_references_acyclic", violations.Count == 0,
            violations.Count == 0
                ? $"{projects.Length} 个项目引用方向正确"
                : "存在反向项目引用：" + string.Join("；", violations.Distinct().Take(6))));
        return checks;
    }

    private static string? ResolveModule(string ns, string? baseName)
        => Rank.ContainsKey(ns) ? ns                                  // 新方案：HuTao.Foundation / HuTao.Dialogue.Core …
           : ns.StartsWith("HuTao.Dialogue", StringComparison.Ordinal) ? "Dialogue"
           : ns.StartsWith("HuTao.Knowledge", StringComparison.Ordinal) ? "Knowledge"
           : ns.StartsWith("HuTao.Persona", StringComparison.Ordinal) ? "Persona"
           : ns.StartsWith("HuTao.Voice", StringComparison.Ordinal) ? "Voice"
           : ns.StartsWith("HuTao.Bridge", StringComparison.Ordinal) ? "Bridge"
           : ns == "HuTao.Dialogue.Runtime" && baseName is not null
                ? (RuntimeFiles.TryGetValue(baseName, out var m) ? m : null)
           : LegacyNamespaces.TryGetValue(ns, out var legacy) ? legacy
           : null;

    /// <summary>从项目路径判模块：<c>src/HuTao.Knowledge/…</c> 或 <c>hosts/…</c>。</summary>
    private static string? ResolveModuleFromProjectPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (part is "hosts")
                return "Hosts";
            if (Rank.ContainsKey(part))
                return part;
        }
        return null;
    }
}
