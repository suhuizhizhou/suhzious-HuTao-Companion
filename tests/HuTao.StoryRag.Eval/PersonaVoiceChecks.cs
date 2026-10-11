using HuTao.Persona;

/// <summary>
/// 角色语气不得串味。这一组守的是一个**实测发生过的观感缺陷**：
/// 安魂曲会说"本堂主"。
///
/// 根因不是漏写一条台词，而是结构性的——那些台词被写成
/// <c>switch (characterId)</c> 的 <c>default</c> 分支，而 default 本身是胡桃的口吻。
/// 于是**每加一个角色，所有 switch 都会静默地让她说胡桃的话**，测试也不会红。
///
/// 所以这里的断言不是"胡桃说得对"，而是：
/// 1. 每个角色在每个情境下都有自己的台词（不许落到中性兜底）；
/// 2. 任何角色的台词里**不许出现别的角色的专属词**；
/// 3. 中性兜底本身不许含任何角色专属词（它是所有角色共用的安全网）；
/// 4. 引擎源码里不许再出现按角色 id 分支的台词。
/// </summary>
internal static class PersonaVoiceChecks
{
    /// <summary>角色 → 只属于它的词。出现在别人嘴里就是串味。</summary>
    private static readonly (string Id, string Directory, string[] OwnedWords)[] Cast =
    [
        ("hutao", "data/persona/hutao", ["本堂主", "往生堂", "胡桃"]),
        ("furina", "data/persona/furina", ["本水神", "歌剧", "主演", "观众"]),
        ("klee", "data/persona/klee", ["可莉", "蹦蹦", "琴团长", "炸鱼"]),
        ("lacrimosa", "data/nte/lacrimosa/persona", ["安魂曲", "番茄酱", "鉴定师"]),
    ];

    /// <summary>
    /// 引擎/宿主里不允许再出现"按角色 id 选**台词**"的写法。
    ///
    /// 正则刻意要求 <c>=></c> 后面紧跟一个**非 <c>#</c> 开头的字符串字面量**：
    /// 配色表（<c>CharacterThemeCatalog</c>）与 accent 表（<c>SessionFactory.DefaultAccent</c>）
    /// 也是 <c>"hutao" => "#FFE08A3C"</c> 这种形状，但它们不是台词，不该被判红。
    /// 台词一定是自然语言。
    /// </summary>
    private static readonly string[] ForbiddenPatterns =
    [
        @"""(?:hutao|furina|klee|lacrimosa)""\s*=>\s*""[^#]",
    ];

    public static List<CheckResult> Run(string repoRoot)
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok, string detail = "") => checks.Add(new(name, ok, detail));

        var loaded = new Dictionary<string, ScriptedLines>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (id, directory, _) in Cast)
        {
            var root = Path.Combine(repoRoot, directory);
            if (!Directory.Exists(root))
            {
                checks.Add(new CheckResult($"voice_{id}_persona_present", false, directory, Skipped: false));
                continue;
            }

            PersonaProfile profile;
            try
            {
                profile = PersonaLoader.Load(root);
            }
            catch (Exception ex)
            {
                checks.Add(new CheckResult($"voice_{id}_persona_loads", false,
                    $"{ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            loaded[id] = profile.Scripted;
            names[id] = profile.Name;

            // 名字解析必须成功：PersonaLoader 取不到 persona.yaml 的 name 会退回**目录名**，
            // 而安魂曲的目录恰好叫 "persona"。名字错了，后面所有按名分支都会走错。
            Check($"voice_{id}_name_resolved",
                !string.Equals(profile.Name, "persona", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(profile.Name),
                $"name='{profile.Name}'");

            var missing = profile.Scripted.Missing();
            Check($"voice_{id}_has_every_situation_line",
                missing.Count == 0,
                missing.Count == 0
                    ? $"{profile.Scripted.ReadyCount} lines"
                    : "missing: " + string.Join(",", missing));
        }

        // 逐角色逐情境：拿到的是自己的话，且不含别人的专属词。
        foreach (var (id, _, _) in Cast)
        {
            if (!loaded.TryGetValue(id, out var scripted))
                continue;

            var problems = new List<string>();
            foreach (var situation in Enum.GetValues<ScriptedLines.Situation>())
            {
                var text = scripted.Get(situation);
                if (string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{situation}=empty");
                    continue;
                }
                if (text == ScriptedLines.Neutral(situation))
                {
                    problems.Add($"{situation}=neutral(未生成)");
                    continue;
                }
                foreach (var (otherId, _, ownedWords) in Cast)
                {
                    if (otherId == id)
                        continue;
                    foreach (var word in ownedWords)
                        if (text.Contains(word, StringComparison.Ordinal))
                            problems.Add($"{situation} 出现『{word}』(属于 {otherId})");
                }
            }

            Check($"voice_{id}_never_borrows_another_character",
                problems.Count == 0,
                problems.Count == 0 ? "clean" : string.Join(" | ", problems));
        }

        // 中性兜底是所有角色共用的安全网：一旦它带角色特征，整个抽象就白做了。
        var neutralLeaks = new List<string>();
        foreach (var situation in Enum.GetValues<ScriptedLines.Situation>())
        {
            var text = ScriptedLines.Neutral(situation) + ScriptedLines.Describe(situation);
            foreach (var (ownerId, _, ownedWords) in Cast)
                foreach (var word in ownedWords)
                    if (text.Contains(word, StringComparison.Ordinal))
                        neutralLeaks.Add($"{situation} 含『{word}』({ownerId})");
        }
        Check("voice_neutral_fallback_is_character_agnostic",
            neutralLeaks.Count == 0,
            neutralLeaks.Count == 0 ? "clean" : string.Join(" | ", neutralLeaks));

        // 生成提示词必须带人设，否则补出来的台词像路人；而且必须只要一句话。
        if (loaded.TryGetValue("hutao", out var hutao) &&
            File.Exists(Path.Combine(repoRoot, "data/persona/hutao/system-prompt.md")))
        {
            var prompt = hutao.BuildPrompt(ScriptedLines.Situation.IgnoredMild);
            var systemPrompt = File.ReadAllText(
                Path.Combine(repoRoot, "data/persona/hutao/system-prompt.md")).Trim();
            var head = systemPrompt.Length >= 20 ? systemPrompt[..20] : systemPrompt;
            Check("voice_generation_prompt_carries_persona",
                prompt.Contains(head, StringComparison.Ordinal), $"len={prompt.Length}");
            Check("voice_generation_prompt_demands_one_line",
                prompt.Contains("只输出你开口说的那一句话本身", StringComparison.Ordinal));
        }

        // 产品代码里不许再按角色 id 选台词。
        var offenders = new List<string>();
        foreach (var file in EnumerateProductSources(repoRoot))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            foreach (var pattern in ForbiddenPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)} ~ {pattern}");
            }
        }
        Check("voice_product_code_has_no_per_character_lines",
            offenders.Count == 0,
            offenders.Count == 0 ? "clean" : string.Join(" | ", offenders.Take(5)));

        return checks;
    }

    /// <summary>src/ 与 hosts/ 下的产品源码（不含测试与生成物）。</summary>
    private static IEnumerable<string> EnumerateProductSources(string repoRoot)
    {
        foreach (var top in new[] { "src", "hosts" })
        {
            var root = Path.Combine(repoRoot, top);
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;
                yield return file;
            }
        }
    }
}
