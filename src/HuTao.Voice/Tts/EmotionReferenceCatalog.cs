using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuTao.Voice;

/// <summary>一次合成最终采用的参考声线与推理预设。</summary>
public sealed record EmotionVoiceStyle(
    string Emotion,
    string RefAudioPath,
    string RefText,
    double SpeedFactor,
    double Temperature);

/// <summary>从角色目录加载多情绪参考音频，并在缺失时安全回退到默认参考。</summary>
public sealed class EmotionReferenceCatalog
{
    private readonly Dictionary<string, EmotionProfile> _profiles;
    private readonly string _catalogDirectory;
    private readonly string _fallbackAudio;
    private readonly string _fallbackText;
    private readonly Dictionary<string, string> _lastReferenceByEmotion =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _selectionGate = new();

    private EmotionReferenceCatalog(
        Dictionary<string, EmotionProfile> profiles,
        string catalogDirectory,
        string fallbackAudio,
        string fallbackText)
    {
        _profiles = profiles;
        _catalogDirectory = catalogDirectory;
        // 允许没有默认参考音频：角色完全没有语音数据时应当降级为文字模式，
        // 而不是让 Path.GetFullPath("") 抛 ArgumentException 把整个角色加载搞崩。
        // 自定义角色（人设目录里没有参考音频）也会走这条路径。
        _fallbackAudio = string.IsNullOrWhiteSpace(fallbackAudio)
            ? ""
            : Path.GetFullPath(fallbackAudio);
        _fallbackText = fallbackText;
    }

    public static EmotionReferenceCatalog Load(
        string catalogPath,
        string fallbackAudio,
        string fallbackText)
    {
        var fullPath = Path.GetFullPath(catalogPath);
        if (!File.Exists(fullPath))
            return new EmotionReferenceCatalog([], Path.GetDirectoryName(fullPath)!, fallbackAudio, fallbackText);

        try
        {
            var document = JsonSerializer.Deserialize<EmotionCatalogDocument>(
                File.ReadAllText(fullPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            return new EmotionReferenceCatalog(
                document?.Emotions ?? [], Path.GetDirectoryName(fullPath)!, fallbackAudio, fallbackText);
        }
        catch (JsonException)
        {
            return new EmotionReferenceCatalog([], Path.GetDirectoryName(fullPath)!, fallbackAudio, fallbackText);
        }
    }

    public EmotionVoiceStyle Select(string? emotion, double intensity)
    {
        var normalized = SpeechEmotion.Normalize(emotion);
        var selectedEmotion = FindUsableProfile(normalized) is not null ? normalized : "neutral";
        var profile = FindUsableProfile(selectedEmotion);
        if (profile is null)
            return new EmotionVoiceStyle("neutral", _fallbackAudio, _fallbackText, 1.0, 0.6);

        var usable = profile.References
            .Select(reference => (Reference: reference, Path: ResolvePath(reference.Audio)))
            .Where(item => File.Exists(item.Path) && !string.IsNullOrWhiteSpace(item.Reference.Text))
            .Where(item => IsReferenceLengthUsable(item.Path))
            .ToList();
        if (usable.Count == 0)
            return new EmotionVoiceStyle("neutral", _fallbackAudio, _fallbackText, 1.0, 0.6);

        lock (_selectionGate)
        {
            if (usable.Count > 1 &&
                _lastReferenceByEmotion.TryGetValue(selectedEmotion, out var lastPath))
                usable.RemoveAll(item =>
                    string.Equals(item.Path, lastPath, StringComparison.OrdinalIgnoreCase));

            var selected = usable[Random.Shared.Next(usable.Count)];
            _lastReferenceByEmotion[selectedEmotion] = selected.Path;
            var amount = Math.Clamp(intensity, 0, 1);
            var speed = 1.0 + (profile.SpeedFactor - 1.0) * amount;
            var temperature = 0.6 + (profile.Temperature - 0.6) * amount;
            return new EmotionVoiceStyle(
                selectedEmotion, selected.Path, selected.Reference.Text,
                Math.Clamp(speed, 0.8, 1.15), Math.Clamp(temperature, 0.4, 0.9));
        }
    }

    /// <summary>当前角色是否至少配置了一条实际存在的参考音频。</summary>
    public bool HasUsableReference
        => _profiles.Values
            .SelectMany(profile => profile.References)
            .Any(reference => File.Exists(ResolvePath(reference.Audio)) &&
                              !string.IsNullOrWhiteSpace(reference.Text));

    /// <summary>
    /// GPT-SoVITS 对参考音频的硬性要求是 **3~10 秒**，越界时
    /// <c>inference_webui.py</c> 会直接 <c>raise OSError("参考音频在3~10秒范围外，请更换！")</c>，
    /// 表现到界面上只是「这一句没有语音」——因为合成失败被上层吞成了文字回复。
    ///
    /// 所以这里主动把越界的参考音频挑出去。**一条坏参考不该让整个情绪没声音**：
    /// 同一情绪通常配了 3~4 条，滤掉一条还剩别的；全被滤掉时退回默认参考。
    ///
    /// 同理，默认参考本身越界时也只能退回「无参考」——那会让 GPT-SoVITS 用它自己的
    /// 默认音色，音色不对但至少能出声，比静默失败要好。
    /// </summary>
    public static bool IsReferenceLengthUsable(string path)
        => TryReadWavSeconds(path) is not { } seconds || (seconds >= 3.0 && seconds <= 10.0);

    /// <summary>读 WAV 头算时长。读不出来时返回 null，由调用方决定怎么处理（这里选择放行）。</summary>
    public static double? TryReadWavSeconds(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 44)
                return null;
            if (new string(reader.ReadChars(4)) != "RIFF" || new string(reader.ReadChars(4)) != "WAVE")
                return null;

            // 顺序扫 chunk，别假设 fmt 一定紧跟 WAVE 头。
            while (stream.Position + 8 <= stream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadInt32();
                if (size < 0)
                    return null;
                if (id == "fmt ")
                {
                    reader.ReadUInt16();                 // audioFormat
                    var channels = reader.ReadUInt16();
                    var sampleRate = reader.ReadUInt32();
                    reader.ReadUInt32();                 // byteRate
                    reader.ReadUInt16();                 // blockAlign
                    var bits = reader.ReadUInt16();
                    if (channels == 0 || sampleRate == 0 || bits == 0)
                        return null;
                    // 数据长度未必等于文件剩余长度（后面可能还有 chunk），但足够近似。
                    var dataBytes = Math.Max(0, stream.Length - stream.Position - 8);
                    return (double)dataBytes / (sampleRate * channels * (bits / 8.0));
                }
                stream.Seek(size + (size % 2), SeekOrigin.Current);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private EmotionProfile? FindUsableProfile(string emotion)
        => _profiles.TryGetValue(emotion, out var profile) && profile.References.Count > 0
            ? profile
            : null;

    private string ResolvePath(string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_catalogDirectory, path));
}

public static class SpeechEmotion
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "neutral", "cheerful", "teasing", "concerned", "angry", "sleepy",
    };

    public static string Normalize(string? value)
    {
        var emotion = value?.Trim().ToLowerInvariant() ?? "neutral";
        return emotion switch
        {
            "开心" or "高兴" or "活泼" or "happy" => "cheerful",
            "俏皮" or "调皮" or "傲娇" or "playful" => "teasing",
            "关心" or "安慰" or "温柔" or "sad" or "gentle" => "concerned",
            "生气" or "不满" or "严肃" or "mad" => "angry",
            "困倦" or "疲惫" or "轻声" or "tired" => "sleepy",
            _ when Supported.Contains(emotion) => emotion,
            _ => "neutral",
        };
    }
}

internal sealed class EmotionCatalogDocument
{
    [JsonPropertyName("emotions")]
    public Dictionary<string, EmotionProfile> Emotions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class EmotionProfile
{
    [JsonPropertyName("speed_factor")] public double SpeedFactor { get; set; } = 1.0;
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.6;
    [JsonPropertyName("references")] public List<EmotionReference> References { get; set; } = [];
}

internal sealed class EmotionReference
{
    [JsonPropertyName("audio")] public string Audio { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}
