using HuTao.Dialogue.Core;

/// <summary>直接验证纯策略，避免 Stub 的固定回答掩盖提示约束遗漏。</summary>
internal static class ResponseRepairChecks
{
    public static List<CheckResult> Run()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok) => checks.Add(new(name, ok, ""));
        string[] constraints = ["只返回 JSON", "绑定 evidence_ids", "明确说出不确定的部分", "原话不得改写", "原话不得改写"];
        var relaxed = ResponseRepairPolicy.BuildRetryConstraint(constraints, "你认识钟离吗？", false, true);
        Check("repair_relaxed_retains_uncertainty", relaxed.Contains("- 明确说出不确定的部分"));
        Check("repair_relaxed_retains_quote_constraint", relaxed.Contains("- 原话不得改写"));
        Check("repair_relaxed_removes_protocol_only", !relaxed.Contains("JSON") && !relaxed.Contains("evidence_ids"));
        Check("repair_deduplicates_instructions", relaxed.Split("- 原话不得改写").Length == 2);
        Check("repair_retains_user_question", relaxed.Contains("你认识钟离吗？"));
        Check("repair_retains_grounding", relaxed.Contains("资料里没有的不要补"));
        var strict = ResponseRepairPolicy.BuildRetryConstraint(constraints, "问题", false, false);
        Check("repair_strict_retains_protocol", strict.Contains("- 只返回 JSON") && strict.Contains("- 绑定 evidence_ids"));
        Check("repair_empty_strict_no_retry", ResponseRepairPolicy.BuildRetryConstraint([], null, false, false) == "");
        var proactive = ResponseRepairPolicy.BuildRetryConstraint(constraints, null, true, true);
        Check("repair_proactive_no_invented_question", proactive.Contains("没有用户提问") && !proactive.Contains("用户刚说"));
        Check("repair_empty_relaxed_still_grounded", ResponseRepairPolicy.BuildRetryConstraint([], null, false, true).Contains("资料里没有的不要补"));
        Check("repair_null_answer_no_instructions", ResponseRepairPolicy.DegradedInstructions(null).Count == 0);
        Check("repair_no_verdict_preserves_query", ResponseRepairPolicy.ReviseRetrievalQuery("钟离", null) == "钟离");
        return checks;
    }
}
