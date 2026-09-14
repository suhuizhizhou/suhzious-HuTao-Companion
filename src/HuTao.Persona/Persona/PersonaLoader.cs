using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HuTao.Persona;

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
        var personaCardPath = Path.Combine(root, "persona.yaml");
        if (!File.Exists(systemPromptPath))
            throw new FileNotFoundException($"缺少 system-prompt.md: {systemPromptPath}");

        var systemPrompt = File.ReadAllText(systemPromptPath);
        var catchphrases = new Catchphrases();
        if (File.Exists(catchphrasesPath))
        {
            catchphrases = JsonSerializer.Deserialize<Catchphrases>(
                File.ReadAllText(catchphrasesPath)) ?? new Catchphrases();
        }

        var lore = File.Exists(lorePath) ? ReadLoreForPrompt(File.ReadAllText(lorePath)) : "";
        var name = Path.GetFileName(root);
        if (File.Exists(personaCardPath))
        {
            var match = Regex.Match(
                File.ReadAllText(personaCardPath), @"(?m)^\s*name:\s*(?<name>.+?)\s*$");
            if (match.Success)
                name = match.Groups["name"].Value.Trim().Trim('"', '\'');
        }
        var quotes = new List<SignatureVoice>();
        if (File.Exists(quotesPath))
        {
            var doc = JsonSerializer.Deserialize<QuotesDoc>(File.ReadAllText(quotesPath));
            quotes = doc?.SignatureVoices ?? [];
        }

        return new PersonaProfile
        {
            Root = root,
            Name = name,
            SystemPrompt = systemPrompt,
            Catchphrases = catchphrases,
            Lore = lore,
            Quotes = quotes,
            // 词表可选：缺了只是剧情检索不再认识这个角色的自称与别名，不影响启动。
            Lexicon = StoryLexicon.Load(Path.Combine(root, "lexicon.json")) ?? StoryLexicon.Empty,
        };
    }

    /// <summary>
    /// 只把 lore.md 里标注为「供提示词注入」的那一段交给模型。
    ///
    /// 为什么需要这一刀：lore.md 同时装着**给人看的参考材料**（游戏原文长段落、来源分级、
    /// 「原文没交代的事」清单）。整份塞进 system prompt 有两个问题——
    /// 一是它会长到十几 KB（每次调用都烧一遍），
    /// 二是那些材料里有大量**角色本人并不知道**的东西（档案旁白、传闻、伏笔），
    /// 模型会把它们当成「我的生平」照单全收，于是说出她本不该知道的事。
    ///
    /// 标记缺失时退回「整份都注入」，保持对老角色（胡桃/芙宁娜/可莉）的兼容。
    /// </summary>
    public static string ReadLoreForPrompt(string loreText)
    {
        const string begin = "<!-- PROMPT:BEGIN -->";
        const string end = "<!-- PROMPT:END -->";
        var start = loreText.IndexOf(begin, StringComparison.Ordinal);
        if (start < 0)
            return loreText;
        var stop = loreText.IndexOf(end, start, StringComparison.Ordinal);
        if (stop < 0)
            return loreText;
        return loreText[(start + begin.Length)..stop].Trim();
    }
}
