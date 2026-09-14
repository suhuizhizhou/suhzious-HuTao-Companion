using System.Text.Json.Serialization;

namespace HuTao.Persona;

/// <summary>加载好的人设包：系统提示词 + 口头禅库 + 生平 + 招牌语音 + 词表。</summary>
public sealed class PersonaProfile
{
    public required string Root { get; init; }
    public required string Name { get; init; }
    public required string SystemPrompt { get; init; }
    public required Catchphrases Catchphrases { get; init; }
    public required string Lore { get; init; }
    public required IReadOnlyList<SignatureVoice> Quotes { get; init; }
    /// <summary>
    /// 角色词表（lexicon.json，可选）。剧情检索靠它认识「本堂主」「桃桃」「大咪」这类说法；
    /// 缺省时检索照常工作，只是不再做角色专属判断。
    /// </summary>
    public StoryLexicon Lexicon { get; init; } = StoryLexicon.Empty;

    /// <summary>
    /// 实际交给检索的词表。词表里没写自称时，至少把角色名当作自称——
    /// 名字是最常出现的那个词，缺了它闲聊会被误判成剧情提问。
    /// </summary>
    public StoryLexicon EffectiveLexicon => Lexicon.WithSelfNameFallback(Name);
}

/// <summary>胡桃说过的重要短促语音（quotes.json），带标签+文字，供一字不差复刻。</summary>
public sealed class SignatureVoice
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("source_file")] public string SourceFile { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }
}

/// <summary>口头禅库（对应 data/persona/catchphrases.json）。</summary>
public sealed class Catchphrases
{
    [JsonPropertyName("laughter")] public List<string> Laughter { get; set; } = [];
    [JsonPropertyName("particles")] public List<string> Particles { get; set; } = [];
    [JsonPropertyName("exclamations")] public List<string> Exclamations { get; set; } = [];
    [JsonPropertyName("self_reference")] public List<string> SelfReference { get; set; } = [];
    [JsonPropertyName("business_lines")] public List<string> BusinessLines { get; set; } = [];
    [JsonPropertyName("life_death_lines")] public List<string> LifeDeathLines { get; set; } = [];
    [JsonPropertyName("signature_quotes")] public List<string> SignatureQuotes { get; set; } = [];
}
