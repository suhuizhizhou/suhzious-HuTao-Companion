namespace HuTao.Foundation;

/// <summary>宿主共用的应用根目录定位与 .env 加载工具，兼容源码仓库和发布包布局。</summary>
public static class ProjectEnvironment
{
    /// <summary>
    /// 从给定起点向上找仓库根（或发布包根）。
    ///
    /// 判据是**目录组合**而不是某个标志文件，因为它要同时兼容两种布局：
    /// - 源码仓库：<c>src/</c> + <c>voice/</c>
    /// - 发布包：<c>data/persona/</c> + <c>voice/infer/</c>
    ///
    /// ⚠️ 这里踩过两次坑，都是**目录被挪走**导致的，而且症状都是「桌宠启动即崩」：
    /// 一次是把 CORE.md 移进 notes/；一次是模块化拆分时 <c>agent/</c> 被拆成
    /// <c>src/</c> + <c>hosts/</c>。所以判据里保留了 <c>agent/</c> 作为旧布局的兼容项——
    /// 迁移期同时存在两种布局时都能定位。
    /// </summary>
    public static string? FindRepositoryRoot(params string[] starts)
    {
        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var repositoryLayout = Directory.Exists(Path.Combine(dir.FullName, "voice")) &&
                                       (Directory.Exists(Path.Combine(dir.FullName, "src")) ||
                                        Directory.Exists(Path.Combine(dir.FullName, "agent")));
                var releaseLayout = Directory.Exists(Path.Combine(dir.FullName, "data", "persona")) &&
                                    Directory.Exists(Path.Combine(dir.FullName, "voice", "infer"));
                if (repositoryLayout || releaseLayout)
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    public static bool LoadDotEnv(
        IEnumerable<string>? candidates = null,
        Action<string>? log = null)
    {
        var paths = candidates ??
        [
            Path.Combine(AppContext.BaseDirectory, ".env"),
            ".env",
            "../.env",
            "../../../../.env",
            "../../../../../.env",
            "../../../../../../.env",
        ];
        foreach (var candidate in paths)
        {
            if (!File.Exists(candidate))
                continue;

            foreach (var line in File.ReadAllLines(candidate))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#'))
                    continue;
                var separator = text.IndexOf('=');
                if (separator <= 0)
                    continue;
                var key = text[..separator].Trim();
                var value = text[(separator + 1)..].Trim();
                if (Environment.GetEnvironmentVariable(key) is null && value.Length > 0)
                    Environment.SetEnvironmentVariable(key, value);
            }
            log?.Invoke($"[env] 已加载 {Path.GetFullPath(candidate)}");
            return true;
        }
        return false;
    }
}
