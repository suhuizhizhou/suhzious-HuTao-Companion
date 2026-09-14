using CUE4Parse_Unused = System.Object;
using HuTao.Knowledge.Rag;

/// <summary>
/// 异环(NTE) 剧情 RAG 的离线回归。
///
/// 关键点：这套用例**不碰 CUE4Parse**，只吃 tools/nte 产出的语料。
/// 因为 `StoryRagService` 从一开始就是角色无关的（只认 dialogueRoot 和语料 schema），
/// 把 NTE 文本适配成同一套 schema 之后，整条 BM25 + 查询改写 + RRF + 充分性判定
/// 的链路是白拿的——这正是「一脉相承」的收益。
///
/// 语料在公开仓库里是 gitignore 的，缺失时整组跳过而不是失败。
/// </summary>
internal static class NteChecks
{
    public static async Task<List<CheckResult>> RunAsync(string repoRoot)
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool? ok, string detail = "") => checks.Add(CheckResult.Of(name, ok, detail));

        var dialogueRoot = Path.Combine(repoRoot, "data", "nte", "_corpus", "dialogue");
        if (!Directory.Exists(Path.Combine(dialogueRoot, "chapters")))
        {
            Check("nte_corpus_available", null, "本地无 NTE 语料（tools/nte 产出，已 gitignore）");
            return checks;
        }

        try
        {
            var summaries = StoryVectorStore.Load(Path.Combine(repoRoot, "data", "nte", "_corpus", "index.json"));
            var service = new StoryRagService(dialogueRoot, summaries);
            await service.WarmupAsync();
            var stats = await service.GetStatsAsync();

            Check("nte_corpus_loads", stats.SearchableLines > 1000,
                $"可检索行 {stats.SearchableLines} / 缺行 {stats.MissingLines} / 文件 {stats.Files}");

            // 说话人解析：语料里应当出现真实角色名而不是 NPC 代号。
            Check("nte_speaker_names_resolved",
                stats.SearchableLines > 0 && stats.Warnings.All(w => !w.StartsWith("Conflicting")),
                $"warnings={stats.Warnings.Count}");

            // 检索主干：拿一句确定存在于语料里的原文去问，应当能命中。
            var probe = "塔吉多";   // 带剧情标记，避开硬编码词表的路由限制
            var hit = await service.RetrieveAsync($"剧情里 {probe} 是谁，给我台词原文");
            Check("nte_retrieval_finds_evidence",
                hit.Status is StoryStatus.Answer or StoryStatus.Tentative && hit.Evidence.Count > 0,
                $"status={hit.Status}; evidence={hit.Evidence.Count}");

            // 场景窗口：异环语料没有分支链，Context() 应当退回序号窗口并给出上下文。
            var top = hit.Evidence.FirstOrDefault();
            Check("nte_scene_window_degrades_to_sequence",
                top is not null && top.Context.Count >= 1,
                top is null ? "no evidence" : $"context={top.Context.Count}; kind={top.ContextKind}");

            // 语料里没有的内容不该编出证据。
            var miss = await service.RetrieveAsync("量子纠缠的数学定义是什么");
            Check("nte_retrieval_does_not_fabricate",
                miss.Status is StoryStatus.NotFound or StoryStatus.Bypass or StoryStatus.Clarify,
                $"status={miss.Status}");

            // 全角/半角与标点不该影响召回（沿用桌宠既有的归一化）。
            var punctuated = await service.RetrieveAsync("「塔吉多」的台词原文是哪句");
            Check("nte_normalization_robust",
                punctuated.Evidence.Count > 0,
                $"evidence={punctuated.Evidence.Count}");

            checks.AddRange(CheckVoiceManifestSchema(repoRoot));
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("nte_unexpected", false, ex.GetType().Name + ": " + ex.Message));
        }

        return checks;
    }

    /// <summary>
    /// 原声清单的**格式契约**检查。
    ///
    /// 这条是补写的，因为踩过一次「静默失效」：tools/nte 早先按默认序列化写出
    /// PascalCase（`Id`/`Audio`/`DurationMs`），而桌宠侧 <c>VoiceManifestEntry</c> 映射的
    /// 是 `id`/`audio`/`duration_ms`。反序列化不抛异常，只是字段全为默认值，
    /// 于是 368 条语音一条都用不上——日志上看不出任何问题。
    ///
    /// 所以这里不复制过滤规则，而是**直接调用真正的读取入口**
    /// <see cref="HuTao.Persona.OriginalVoiceCatalog.Load"/>：
    /// 只要它读不出条目，就说明导出方与消费方的 schema 又漂了。
    /// 语音目录是 gitignore 的，缺失时跳过而不是失败。
    /// </summary>
    private static List<CheckResult> CheckVoiceManifestSchema(string repoRoot)
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool? ok, string detail = "") => checks.Add(CheckResult.Of(name, ok, detail));

        var nteRoot = Path.Combine(repoRoot, "data", "nte");
        if (!Directory.Exists(nteRoot))
            return checks;

        var manifests = Directory.EnumerateFiles(nteRoot, "manifest.jsonl", SearchOption.AllDirectories)
            .ToArray();
        if (manifests.Length == 0)
        {
            Check("nte_voice_manifest_schema", null, "本地无 NTE 语音清单");
            return checks;
        }

        // 必需字段的写法（与桌宠 data/voice/<id>/manifest.jsonl 一致）
        string[] required = ["id", "audio", "text", "source_file", "duration_ms"];

        foreach (var manifest in manifests)
        {
            var parent = Path.GetDirectoryName(manifest);
            var character = parent is null ? "?" : Path.GetFileName(Path.GetDirectoryName(parent)) ?? "?";

            var lines = File.ReadLines(manifest).Count(l => !string.IsNullOrWhiteSpace(l));
            if (lines == 0)
            {
                // 合法状态：该角色在本机确实没有语音（例如安魂曲）。记为「未评估」，
                // 不计入通过数——清单为空不能充当「schema 正确」的证据。
                Check($"nte_voice_manifest_keys[{character}]", null, "清单为空（该角色本机无语音）");
                continue;
            }

            var firstLine = File.ReadLines(manifest).First(l => !string.IsNullOrWhiteSpace(l));
            var missing = new List<string>();
            using (var document = System.Text.Json.JsonDocument.Parse(firstLine))
            {
                foreach (var key in required)
                    if (!document.RootElement.TryGetProperty(key, out _))
                        missing.Add(key);
            }
            Check($"nte_voice_manifest_keys[{character}]", missing.Count == 0,
                missing.Count == 0 ? "字段名与桌宠一致" : $"缺少 {string.Join('/', missing)}");

            // 端到端：用消费方真正的读取入口。
            // 只有清单足够长（≥20 条）才要求读出条目——内容过滤（括号独白、时长上限）
            // 本来就会滤掉一部分，短清单里可能一条都不剩，那不是 schema 问题。
            var catalog = HuTao.Persona.OriginalVoiceCatalog.Load(manifest);
            if (lines >= 20)
                Check($"nte_voice_manifest_loadable[{character}]", catalog.HasEntries,
                    catalog.HasEntries
                        ? $"{lines} 行 → 原声 {catalog.All.Count} 条"
                        : $"{lines} 行一条都读不出 —— 导出格式与消费方 schema 不一致（曾经踩过）");
        }

        return checks;
    }
}
