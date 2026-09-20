using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HuTao.Foundation.Abstractions;
using Microsoft.VisualBasic.FileIO;

namespace HuTao.Dialogue.Tools;

public static class BuiltinToolCatalog
{
    private static ToolArgument Text(string name, int max = 12000, bool required = true) => new(name, ToolArgumentType.String, required, max);
    private static ToolArgument Choice(string name, params string[] options) => new(name, ToolArgumentType.String, Enum: options);
    private static ToolArgument Number(string name) => new(name, ToolArgumentType.Number);
    private static string S(JsonElement a, string key) => a.GetProperty(key).GetString()!;
    private static double N(JsonElement a, string key) => a.GetProperty(key).GetDouble();
    private static string Json(object value) => JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static IReadOnlyList<IAgentTool> Create(string workspaceRoot)
    {
        var workspace = new ToolWorkspace(workspaceRoot);
        var outputPolicy = new AgentToolPolicy(AgentToolEffect.LocalOutput, RequiresExplicitIntent: true,
            CanRunInParallel: false, RequiresConfirmation: true);
        string? CheckPath(JsonElement a, bool write = false, bool document = false)
        {
            var path = workspace.Resolve(S(a, "path"));
            if (!ToolWorkspace.TextExtensions.Contains(Path.GetExtension(path)) &&
                !(document && Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase)))
                return "unsupported_extension";
            if (write && File.Exists(path)) return "output_already_exists";
            if (!write && !File.Exists(path)) return "file_not_found";
            return null;
        }
        return
        [
            new BuiltinTool("calculator", "四则运算、余数、幂；无脚本或表达式执行。",
                new(Number("left"), new ToolArgument("operation", ToolArgumentType.String,
                    Enum: ["add", "subtract", "multiply", "divide", "remainder", "power"],
                    Description: "加=add；减=subtract；乘/乘以=multiply；除以=divide；取余=remainder；次方=power"), Number("right")), a =>
                {
                    var left = N(a, "left"); var right = N(a, "right");
                    var value = S(a, "operation") switch
                    {
                        "add" => left + right, "subtract" => left - right, "multiply" => left * right,
                        "divide" when right != 0 => left / right, "remainder" when right != 0 => left % right,
                        "power" => Math.Pow(left, right), _ => throw new ArgumentException("division_by_zero")
                    };
                    if (!double.IsFinite(value)) throw new ArithmeticException("non_finite_result");
                    return Json(new { left, operation = S(a, "operation"), right, value });
                }),
            new BuiltinTool("unit_convert", "长度、质量、时间、温度单位转换；不同量纲不能互转。",
                new(Number("value"), Choice("from", Units.Keys.ToArray()), Choice("to", Units.Keys.ToArray())), a =>
                {
                    var from = Units[S(a, "from")]; var to = Units[S(a, "to")];
                    if (from.Dimension != to.Dimension) throw new ArgumentException("incompatible_units");
                    return Json(new { value = (N(a, "value") * from.Scale + from.Offset - to.Offset) / to.Scale, unit = S(a, "to") });
                }),
            new BuiltinTool("date_offset", "按天数推算日期，输入 yyyy-MM-dd。",
                new(Text("date", 10), new("days", ToolArgumentType.Integer, Minimum: -36500, Maximum: 36500)), a =>
                    Json(new { date = Date(a, "date").AddDays(a.GetProperty("days").GetInt32()).ToString("yyyy-MM-dd"),
                        days = a.GetProperty("days").GetInt32() })),
            new BuiltinTool("date_difference", "计算两个日期间的天数，输入 yyyy-MM-dd，结果=end-start。",
                new(Text("start", 10), Text("end", 10)), a => Json(new { days = Date(a, "end").DayNumber - Date(a, "start").DayNumber })),
            new BuiltinTool("timezone_convert", "把带明确 UTC 偏移的 ISO 时间转换到 Windows 或 IANA 时区。",
                new(Text("timestamp", 40), Text("timezone", 80)), a =>
                    Json(new { timestamp = TimeZoneInfo.ConvertTime(Instant(S(a, "timestamp")),
                        TimeZoneInfo.FindSystemTimeZoneById(S(a, "timezone"))).ToString("O") })),
            new BuiltinTool("json_format", "校验并格式化用户提供的 JSON；无文件读取。", new(Text("text")), a =>
                {
                    using var doc = JsonDocument.Parse(S(a, "text"), new JsonDocumentOptions { MaxDepth = 32 });
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }),
            new BuiltinTool("csv_analyze", "解析 CSV/TSV 引号和换行；统计行列及不等宽行，返回前5行。",
                new(Text("text"), Choice("delimiter", ",", "\t", ";")), a =>
                {
                    using var parser = new TextFieldParser(new StringReader(S(a, "text"))) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
                    parser.SetDelimiters(S(a, "delimiter"));
                    var rows = new List<string[]>();
                    while (!parser.EndOfData && rows.Count < 2000) rows.Add(parser.ReadFields()!);
                    var columns = rows.FirstOrDefault()?.Length ?? 0;
                    return Json(new { rows = rows.Count, columns, inconsistent_rows = rows.Select((r, i) => (r, i))
                        .Where(x => x.r.Length != columns).Select(x => x.i + 1), preview = rows.Take(5), truncated = !parser.EndOfData });
                }),
            new BuiltinTool("regex_match", "正则匹配用户提供的文本，最多50条结果，250ms 超时。",
                new(Text("text"), Text("pattern", 400)), a =>
                    Json(new { matches = Regex.Matches(S(a, "text"), S(a, "pattern"), RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250)).Cast<Match>().Take(50).Select(m => new { m.Index, m.Length, m.Value }) })),
            new BuiltinTool("text_stats", "统计 Unicode 字符数、行数、空白分词数与 UTF-8 字节数。", new(Text("text")), a =>
                {
                    var text = S(a, "text");
                    return Json(new { unicode_scalars = text.EnumerateRunes().Count(), utf16_length = text.Length,
                        lines = text.Length == 0 ? 0 : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length,
                        whitespace_words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                        utf8_bytes = Encoding.UTF8.GetByteCount(text) });
                }),
            new BuiltinTool("text_transform", "文本大小写、首尾空白清理、按行去重或排序。",
                new(Text("text"), Choice("operation", "upper", "lower", "trim", "unique_lines", "sort_lines")), a =>
                {
                    var text = S(a, "text"); var lines = text.Replace("\r\n", "\n").Split('\n');
                    return Json(new { text = S(a, "operation") switch
                    {
                        "upper" => text.ToUpperInvariant(), "lower" => text.ToLowerInvariant(), "trim" => text.Trim(),
                        "unique_lines" => string.Join('\n', lines.Distinct(StringComparer.Ordinal)),
                        _ => string.Join('\n', lines.Order(StringComparer.Ordinal))
                    } });
                }),
            new BuiltinTool("text_hash", "计算用户提供文本的 UTF-8 SHA-256/SHA-512 摘要。",
                new(Text("text"), Choice("algorithm", "sha256", "sha512")), a => Json(new
                { hash = Convert.ToHexString(S(a, "algorithm") == "sha256" ? SHA256.HashData(Encoding.UTF8.GetBytes(S(a, "text")))
                    : SHA512.HashData(Encoding.UTF8.GetBytes(S(a, "text")))).ToLowerInvariant() })),
            new BuiltinTool("file_list", "列出专用工具目录下的文件名，非递归，最多100项；不访问其他目录。",
                new(Text("directory", 240, false)), a =>
                {
                    var directory = a.TryGetProperty("directory", out var value) && value.GetString() is { Length: > 0 } relative
                        ? workspace.Resolve(relative, true) : workspace.Root;
                    ToolWorkspace.RejectLinks(directory);
                    var items = Directory.EnumerateFileSystemEntries(directory).Take(101).ToArray();
                    return Json(new { root = workspace.Root, entries = items.Take(100).Select(p => Path.GetFileName(p)), truncated = items.Length > 100 });
                }, validate: a =>
                {
                    if (a.TryGetProperty("directory", out var d) && !string.IsNullOrEmpty(d.GetString())) workspace.Resolve(d.GetString()!, true);
                    return null;
                }),
            new BuiltinTool("file_read", "读取专用目录内 UTF-8 文本文件，最大1MiB；只返回前12000字符。",
                new(Text("path", 240)), (a, ct) => workspace.ReadTextAsync(S(a, "path"), ct), validate: a => CheckPath(a)),
            new BuiltinTool("document_extract", "提取专用目录内 TXT/DOCX 段落；不执行宏、外链或嵌入对象。",
                new(Text("path", 240)), (a, ct) => ExtractAsync(workspace.Resolve(S(a, "path")), ct),
                validate: a => CheckPath(a, document: true)),
            new BuiltinTool("file_write", "在专用工具目录创建文本文件；不覆盖已有文件。需用户确认路径与完整内容。",
                new(Text("path", 240), Text("content", 12000)), (a, ct) => workspace.CreateAsync(S(a, "path"), S(a, "content"), ct),
                outputPolicy, a => CheckPath(a, write: true), a => "创建文件（不覆盖）：" + workspace.Resolve(S(a, "path"))),
            new BuiltinTool("calendar_export", "导出本地 .ics 日历事件文件；不修改在线日历、不发邀请、不承诺到点通知。时间必须含UTC偏移。",
                new(Text("path", 240), Text("title", 200), Text("start", 40), Text("end", 40), Text("description", 2000, false)),
                (a, ct) => workspace.CreateAsync(S(a, "path"), Calendar(a), ct), outputPolicy, a =>
                {
                    var error = CheckPath(a, write: true);
                    if (error is not null) return error;
                    if (!S(a, "path").EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) return "expected_ics_extension";
                    return Instant(S(a, "end")) > Instant(S(a, "start")) ? null : "end_must_follow_start";
                }, a => "导出日历文件（不会修改外部日历）：" + workspace.Resolve(S(a, "path")))
        ];
    }

    private static DateOnly Date(JsonElement a, string key) => DateOnly.ParseExact(S(a, key), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset Instant(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$"))
            throw new ArgumentException("explicit_iso_offset_required");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
    private static string Calendar(JsonElement a)
    {
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace('\r', '\n')
            .Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
        static string Fold(string line)
        {
            var b = new StringBuilder(); var bytes = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 75) { b.Append("\r\n "); bytes = 1; }
                b.Append(rune); bytes += rune.Utf8SequenceLength;
            }
            return b.ToString();
        }
        var lines = new[] { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//HuTao Companion//Local Export//ZH",
            "BEGIN:VEVENT", $"UID:{Guid.NewGuid():N}@hutao.local", $"DTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}",
            $"DTSTART:{Instant(S(a, "start")).UtcDateTime:yyyyMMddTHHmmssZ}",
            $"DTEND:{Instant(S(a, "end")).UtcDateTime:yyyyMMddTHHmmssZ}", "SUMMARY:" + Escape(S(a, "title")),
            "DESCRIPTION:" + Escape(a.TryGetProperty("description", out var d) ? d.GetString()! : ""), "END:VEVENT", "END:VCALENDAR" };
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    private static async Task<string> ExtractAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new ArgumentException("document_too_large");
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var workspace = new ToolWorkspace(Path.GetDirectoryName(path)!);
            return await workspace.ReadTextAsync(Path.GetFileName(path), ct).ConfigureAwait(false);
        }
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("missing_document_xml");
        if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException("expanded_document_too_large");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024, Async = true });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = document.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).ToArray();
        var text = string.Join('\n', paragraphs);
        return Json(new { paragraphs = paragraphs.Length, text = text[..Math.Min(12000, text.Length)], truncated = text.Length > 12000 });
    }

    private static readonly IReadOnlyDictionary<string, (string Dimension, double Scale, double Offset)> Units =
        new Dictionary<string, (string, double, double)>
        {
            ["m"] = ("length", 1, 0), ["cm"] = ("length", .01, 0), ["km"] = ("length", 1000, 0),
            ["in"] = ("length", .0254, 0), ["ft"] = ("length", .3048, 0),
            ["kg"] = ("mass", 1, 0), ["g"] = ("mass", .001, 0), ["lb"] = ("mass", .45359237, 0),
            ["s"] = ("time", 1, 0), ["min"] = ("time", 60, 0), ["h"] = ("time", 3600, 0),
            ["C"] = ("temperature", 1, 273.15), ["F"] = ("temperature", 5d / 9, 273.15 - 32 * 5d / 9), ["K"] = ("temperature", 1, 0)
        };
}

internal sealed class BuiltinTool : IAgentTool
{
    private readonly Func<JsonElement, CancellationToken, Task<string>> _execute;
    private readonly Func<JsonElement, string?>? _validate;
    private readonly Func<JsonElement, string>? _describe;
    public BuiltinTool(string name, string description, ToolInputSchema schema, Func<JsonElement, string> execute,
        AgentToolPolicy? policy = null, Func<JsonElement, string?>? validate = null, Func<JsonElement, string>? describe = null)
        : this(name, description, schema, (a, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(execute(a)); }, policy, validate, describe) { }
    public BuiltinTool(string name, string description, ToolInputSchema schema, Func<JsonElement, CancellationToken, Task<string>> execute,
        AgentToolPolicy? policy = null, Func<JsonElement, string?>? validate = null, Func<JsonElement, string>? describe = null)
    { Name = name; Description = description; InputSchema = schema; _execute = execute; Policy = policy ?? new(); _validate = validate; _describe = describe; }
    public string Name { get; }
    public string Description { get; }
    public ToolInputSchema InputSchema { get; }
    public AgentToolPolicy Policy { get; }
    public bool UsesJsonArguments => true;
    public string? ValidateArguments(JsonElement arguments) => _validate?.Invoke(arguments);
    public string DescribeAction(JsonElement arguments) => _describe?.Invoke(arguments) ?? Description;
    public async Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(input ?? "{}");
        var error = InputSchema.Validate(document.RootElement) ?? ValidateArguments(document.RootElement);
        if (error is not null) throw new ArgumentException(error);
        return await _execute(document.RootElement, ct).ConfigureAwait(false);
    }
}
