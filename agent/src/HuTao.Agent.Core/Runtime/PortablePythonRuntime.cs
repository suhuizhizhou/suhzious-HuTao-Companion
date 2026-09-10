using System.Diagnostics;

namespace HuTao.Agent.Core.Runtime;

/// <summary>准备 conda-pack 生成的便携 Python，并统一设置子进程的依赖搜索路径。</summary>
internal static class PortablePythonRuntime
{
    private const string PrefixMarker = ".hutao-runtime-prefix";
    private static readonly object PrepareLock = new();

    public static bool TryPrepare(
        string python,
        string? pythonPath,
        Action<string>? log = null)
    {
        lock (PrepareLock)
            return TryPrepareCore(python, pythonPath, log);
    }

    private static bool TryPrepareCore(
        string python,
        string? pythonPath,
        Action<string>? log)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(python));
        if (root is null)
            return false;

        var unpackScript = Path.Combine(root, "Scripts", "conda-unpack-script.py");
        if (!File.Exists(unpackScript))
            return true;

        var marker = Path.Combine(root, PrefixMarker);
        try
        {
            if (File.Exists(marker) && string.Equals(
                    File.ReadAllText(marker).Trim(),
                    root,
                    StringComparison.OrdinalIgnoreCase))
                return true;

            var psi = new ProcessStartInfo
            {
                // conda-unpack.exe 在部分 Windows 长路径下会把前缀变成 \\?\ 路径，
                // Python 直接执行脚本可保持普通盘符路径。
                FileName = python,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            ConfigureProcess(psi, python, pythonPath);
            psi.ArgumentList.Add(unpackScript);
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(3).TotalMilliseconds) ||
                process.ExitCode != 0)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                log?.Invoke($"[tts] conda-unpack 失败: {stderr.Trim()} {stdout.Trim()}".Trim());
                return false;
            }

            File.WriteAllText(marker, root);
            log?.Invoke($"[tts] 便携 Python 已适配当前目录: {root}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[tts] 初始化便携 Python 失败: {ex.Message}");
            return false;
        }
    }

    public static void ConfigureProcess(
        ProcessStartInfo psi,
        string python,
        string? pythonPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(python));
        if (root is null)
            return;

        var pathEntries = new[]
        {
            root,
            Path.Combine(root, "Scripts"),
            Path.Combine(root, "Library", "bin"),
            Path.Combine(root, "Library", "usr", "bin"),
        };
        _ = psi.Environment.TryGetValue("PATH", out var processPath);
        var existingPath = processPath ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            pathEntries.Where(Directory.Exists).Append(existingPath));

        if (!string.IsNullOrWhiteSpace(pythonPath) && Directory.Exists(pythonPath))
        {
            _ = psi.Environment.TryGetValue("PYTHONPATH", out var processPythonPath);
            var existingPythonPath = processPythonPath
                                     ?? Environment.GetEnvironmentVariable("PYTHONPATH");
            psi.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                ? pythonPath
                : pythonPath + Path.PathSeparator + existingPythonPath;
        }

        psi.Environment["PYTHONNOUSERSITE"] = "1";
        psi.Environment["PYTHONUTF8"] = "1";
    }
}
