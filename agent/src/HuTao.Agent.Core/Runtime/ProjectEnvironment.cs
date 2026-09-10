namespace HuTao.Agent.Core.Runtime;

/// <summary>宿主共用的应用根目录定位与 .env 加载工具，兼容源码仓库和发布包布局。</summary>
public static class ProjectEnvironment
{
    public static string? FindRepositoryRoot(params string[] starts)
    {
        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var repositoryLayout = Directory.Exists(Path.Combine(dir.FullName, "agent")) &&
                                       Directory.Exists(Path.Combine(dir.FullName, "voice"));
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
