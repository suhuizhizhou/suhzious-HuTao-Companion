using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuTao.Agent.Core.Tts;

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
        _fallbackAudio = Path.GetFullPath(fallbackAudio);
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
