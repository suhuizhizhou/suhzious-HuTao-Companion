using System.Text;
using System.Text.RegularExpressions;

namespace HuTao.Dialogue.Tools;

/// <summary>Dedicated local data directory. Never accepts UNC, device, ADS, or linked paths.</summary>
public sealed class ToolWorkspace
{
    public string Root { get; }
    public ToolWorkspace(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RejectLinks(Root);
        Directory.CreateDirectory(Root);
    }

    public string Resolve(string relative, bool directory = false)
    {
        if (relative.Length > 240 || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || relative.Any(char.IsControl)) throw new ArgumentException("invalid_relative_path");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.StartsWith('.') || p.EndsWith('.') || p.EndsWith(' ') ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new ArgumentException("invalid_path_component");
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path_outside_workspace");
        RejectLinks(path);
        if (!directory && Directory.Exists(path)) throw new ArgumentException("expected_file");
        return path;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("linked_path_not_allowed");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public async Task<string> ReadTextAsync(string relative, CancellationToken ct)
    {
        var path = Resolve(relative);
        if (!TextExtensions.Contains(Path.GetExtension(path))) throw new ArgumentException("unsupported_text_extension");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > 1024 * 1024) throw new ArgumentException("file_too_large");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var buffer = new char[12001];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Serialize(new { path = relative, text = new string(buffer, 0, Math.Min(count, 12000)), truncated = count > 12000 });
    }

    public async Task<string> CreateAsync(string relative, string content, CancellationToken ct)
    {
        var path = Resolve(relative);
        if (!TextExtensions.Contains(Path.GetExtension(path))) throw new ArgumentException("unsupported_output_extension");
        if (File.Exists(path)) throw new IOException("output_already_exists");
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        var temporary = Path.Combine(parent, ".tool-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Resolve(relative);
            File.Move(temporary, path, overwrite: false);
            return System.Text.Json.JsonSerializer.Serialize(new { path, bytes = Encoding.UTF8.GetByteCount(content), created = true });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".json", ".csv", ".tsv", ".log", ".ics" };
}
