using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuTao.Agent.Core.Persona;

/// <summary>quotes.json 的顶层结构。</summary>
internal sealed class QuotesDoc
{
    [JsonPropertyName("signature_voices")] public List<SignatureVoice> SignatureVoices { get; set; } = [];
}

/// <summary>从 data/persona 目录加载人设 Skill 包。</summary>
public static class PersonaLoader
{
    public static PersonaProfile Load(string personaRoot)
    {
        var root = Path.GetFullPath(personaRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"人设目录不存在: {root}");

        var systemPromptPath = Path.Combine(root, "system-prompt.md");
        var catchphrasesPath = Path.Combine(root, "catchphrases.json");
        var lorePath = Path.Combine(root, "lore.md");
        var quotesPath = Path.Combine(root, "quotes.json");
        if (!File.Exists(systemPromptPath))
            throw new FileNotFoundException($"缺少 system-prompt.md: {systemPromptPath}");

        var systemPrompt = File.ReadAllText(systemPromptPath);
        var catchphrases = new Catchphrases();
        if (File.Exists(catchphrasesPath))
        {
            catchphrases = JsonSerializer.Deserialize<Catchphrases>(
                File.ReadAllText(catchphrasesPath)) ?? new Catchphrases();
        }

        var lore = File.Exists(lorePath) ? File.ReadAllText(lorePath) : "";
        var quotes = new List<SignatureVoice>();
        if (File.Exists(quotesPath))
        {
            var doc = JsonSerializer.Deserialize<QuotesDoc>(File.ReadAllText(quotesPath));
            quotes = doc?.SignatureVoices ?? [];
        }

        return new PersonaProfile
        {
            Root = root,
            Name = "胡桃",
            SystemPrompt = systemPrompt,
            Catchphrases = catchphrases,
            Lore = lore,
            Quotes = quotes,
        };
    }
}
