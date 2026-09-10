using System.Text.Json;
using HuTao.Agent.Core.Rag;

internal sealed record ExpectedPlanTask(string Id, string Goal, string[] DependsOn);

/// <summary>仅评测金标审计；这些字段绝不传入 Planner 或检索服务。</summary>
internal static class BenchmarkV4Audit
{
    public static void Validate(BenchmarkCase[] cases, string root)
    {
        var rows = cases.Where(c => c.Id.StartsWith("v4-", StringComparison.Ordinal)).ToArray();
        if (rows.Length == 0) return;
        if (rows.Length != 100) throw new InvalidDataException("v4 requires exactly 100 scenario families.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "evaluation/story-rag/benchmark-v4-sources.json")));
        var sources = manifest.RootElement.GetProperty("sources").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var domains = new Dictionary<string, int>
        {
            ["lore"] = 30, ["mixed"] = 20, ["unrelated"] = 15, ["playful"] = 10,
            ["comfort"] = 10, ["clarification"] = 5, ["unknown"] = 10
        };
        foreach (var (domain, count) in domains)
            if (rows.Count(c => c.Domain == domain) != count)
                throw new InvalidDataException($"v4 domain quota mismatch: {domain}");
        if (rows.SelectMany(c => c.Queries).Distinct(StringComparer.Ordinal).Count() != rows.Sum(c => c.Queries.Length))
            throw new InvalidDataException("v4 duplicate question text.");
        foreach (var row in rows)
        {
            void Require(bool valid, string error)
            {
                if (!valid) throw new InvalidDataException($"v4 {row.Id}: {error}");
            }
            Require(row.SourceRefs.All(sources.Contains), "unknown source reference");
            Require(row.SourceRefs.Length == 0 || row.Provenance == "adapted_from_public_search_excerpt", "misleading source provenance");
            Require(row.Rubric.Length > 0 && row.IndependentClaims.Length > 0, "missing answer criteria");
            Require(row.ExpectedPlan.Length > 0, "missing observable task expectations");
            Require(row.Queries.Length == 1, "count scenario families rather than paraphrases");
            Require(row.Route == StoryRoute.Retrieve || row.EvidenceGroups.Length == 0, "non-retrieval case has gold story evidence");
            Require(row.MinimumFactGroups == row.EvidenceGroups.Length, "do not lower coverage for hard cases");
            Require(row.EvidenceGroups.All(g => g.Length > 0), "empty evidence group");
            var tasks = row.ExpectedPlan;
            Require(tasks.All(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Goal) && t.DependsOn is not null), "invalid task");
            var ids = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            Require(ids.Count == tasks.Length, "duplicate task id");
            Require(tasks.All(t => t.DependsOn.All(d => d != t.Id && ids.Contains(d))), "self or dangling dependency");
            var done = new HashSet<string>(StringComparer.Ordinal);
            while (done.Count < tasks.Length)
            {
                var ready = tasks.Where(t => !done.Contains(t.Id) && t.DependsOn.All(done.Contains)).ToArray();
                Require(ready.Length > 0, "cyclic dependency");
                foreach (var task in ready) done.Add(task.Id);
            }
        }
        Console.WriteLine($"DATASET AUDIT v4: {rows.Length} scenarios; {rows.Count(c => c.SourceRefs.Length > 0)} web-inspired; source references and DAGs valid.");
    }
}
