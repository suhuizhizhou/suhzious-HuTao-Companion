using System.Text.RegularExpressions;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.DocumentReading;
using HuTao.Voice;

namespace HuTao.Dialogue.Tools;

/// <summary>
/// Agent 对长文朗读应用服务的薄适配器。
/// 触发判断和用户输入解析留在这里，文档处理本身由 IDocumentReadingService 负责。
/// </summary>
public sealed class DocumentReadingTool : IAgentTool
{
    private static readonly Regex SupportedPath = new(
        @"(?<path>[A-Za-z]:\\[^\r\n""<>|]*?\.(?:txt|docx))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IDocumentReadingService _service;

    /// <summary>使用默认文件实现组装服务，适合桌宠和 Host 的生产装配。</summary>
    public DocumentReadingTool(
        ITtsEngine? tts,
        string? refAudio,
        string? refText,
        EmotionReferenceCatalog? emotionReferences,
        string outputRoot,
        string? ffmpegPath = null,
        string speakerName = "角色",
        Action<string>? progress = null,
        DocumentReadingOptions? options = null)
        : this(new DocumentReadingService(
            tts,
            refAudio,
            refText,
            emotionReferences,
            outputRoot,
            ffmpegPath,
            speakerName,
            progress,
            options))
    {
    }

    /// <summary>注入自定义服务，便于单元测试及未来替换文档处理 pipeline。</summary>
    public DocumentReadingTool(IDocumentReadingService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public string Name => "document_reader";

    public string Description
        => "用户明确要求朗读 TXT/DOCX 或生成 MP3 时，按段落/阶段切分长文，使用当前角色声线和匹配情绪逐段合成，并合并为本地 MP3。";

    public AgentToolPolicy Policy => new(
        Effect: AgentToolEffect.LocalOutput,
        RequiresExplicitIntent: true,
        CanRunInParallel: false, RequiresConfirmation: true, TimeoutSeconds: 600);

    public ToolInputSchema InputSchema => new(new ToolArgument("input", ToolArgumentType.String, MaxLength: 2048));
    public string? ValidateArguments(System.Text.Json.JsonElement arguments) =>
        TryExtractRequestedPath(arguments.GetProperty("input").GetString(), out _) ? null : "explicit_document_request_required";
    public string DescribeAction(System.Text.Json.JsonElement arguments)
    {
        TryExtractRequestedPath(arguments.GetProperty("input").GetString(), out var path);
        return "读取文档并生成本地语音文件：" + path;
    }

    public Task<DocumentReadingResult> ReadAsync(
        string documentPath,
        CancellationToken ct = default)
        => _service.ReadAsync(documentPath, ct);

    public async Task<string> ExecuteAsync(
        string? input = null,
        CancellationToken ct = default)
    {
        if (!TryExtractRequestedPath(input, out var path))
        {
            return "未触发：只有用户明确要求朗读/念稿/生成 MP3 并提供 .txt 或 .docx 路径时才执行。";
        }

        var result = await ReadAsync(path, ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("document_reading_incomplete");
        return $"文档朗读完成：共 {result.ChunkCount} 段，MP3 输出：{result.OutputMp3}。";
    }

    public static bool TryExtractRequestedPath(string? input, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var explicitRequest = input.Contains("朗读", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("读取", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("播报", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("有声", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("念稿", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("生成mp3", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("生成 MP3", StringComparison.OrdinalIgnoreCase) ||
                              input.Contains("read_document", StringComparison.OrdinalIgnoreCase);
        var match = SupportedPath.Match(input);
        if (!match.Success || !explicitRequest)
            return false;

        path = match.Groups["path"].Value.Trim().Trim('"', '\'');
        return true;
    }
}
