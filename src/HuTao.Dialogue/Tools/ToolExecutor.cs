using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Tools;

public enum ToolRunStatus { Succeeded, UnknownTool, InvalidArguments, ActivityDenied, Unauthorized,
    ConfirmationRequired, Declined, BudgetExceeded, Failed, TimedOut, Cancelled }
public enum ToolEffectState { None, NotStarted, Completed, Unknown }
public sealed record ToolRequest(string Name, JsonElement Arguments);
public sealed record ToolApproval(string Tool, string Description, string ArgumentsJson,
    string RequestHash, AgentToolEffect Effect, string UserRequest);
public sealed record ToolRunResult(string CallId, string Tool, ToolRunStatus Status, string Output,
    ToolEffectState SideEffect, double ElapsedMs, string RequestHash, bool Truncated = false);
public sealed record ToolExecutionContext(bool IsUserTurn, string UserRequest,
    bool AllowActivityMetadata = true, bool AllowReadTools = true, bool AllowSideEffects = true);

public sealed class ToolTurnBudget(int maxCalls = 6, int maxOutputCharacters = 16000,
    TimeSpan? maxDuration = null)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _calls;
    private int _output;
    public TimeSpan Remaining => (maxDuration ?? TimeSpan.FromSeconds(45)) - _clock.Elapsed;
    public int OutputRemaining => Math.Max(0, maxOutputCharacters - _output);
    public bool TryReserve() => Remaining > TimeSpan.Zero && OutputRemaining > 0 && _calls++ < maxCalls;
    public void Consume(int characters) => _output += characters;
}

/// <summary>Single gate for requests from deterministic routing or the model.</summary>
public sealed class ToolExecutor(
    Func<ToolApproval, CancellationToken, Task<bool>>? approve = null,
    LocalDiagnosticLog? diagnostics = null)
{
    public async Task<ToolRunResult> ExecuteAsync(IReadOnlyDictionary<string, IAgentTool> tools,
        ToolRequest request, ToolExecutionContext context, ToolTurnBudget budget, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("N");
        var argumentsJson = request.Arguments.ValueKind == JsonValueKind.Undefined ? "null" : request.Arguments.GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Name + "\n" + argumentsJson)));
        var effect = ToolEffectState.NotStarted;
        ToolRunResult Finish(ToolRunStatus status, string output, bool truncated = false)
        {
            var result = new ToolRunResult(id, request.Name, status, output, effect, clock.Elapsed.TotalMilliseconds, hash, truncated);
            (diagnostics ?? LocalDiagnosticLog.Default).Turn(id, "tool", request.Name, status.ToString(),
                effect.ToString(), issues: [hash, truncated ? "truncated" : "complete"]);
            BackendTrace.Current?.Line($"tool {request.Name}: {status}; effect={effect}; {result.ElapsedMs:F0}ms; request={hash}");
            return result;
        }
        // Existence -> schema -> activity -> authorization -> confirmation -> budget -> execution -> audit.
        if (!tools.TryGetValue(request.Name, out var tool)) return Finish(ToolRunStatus.UnknownTool, "工具不存在。");
        if (argumentsJson.Length > 32768) return Finish(ToolRunStatus.InvalidArguments, "参数过长。");
        try
        {
            var error = tool.InputSchema.Validate(request.Arguments) ?? tool.ValidateArguments(request.Arguments);
            if (error is not null) return Finish(ToolRunStatus.InvalidArguments, error);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or KeyNotFoundException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        { return Finish(ToolRunStatus.InvalidArguments, ex.GetType().Name); }
        var policy = tool.Policy;
        var mutates = policy.Effect != AgentToolEffect.ReadOnly;
        if ((!context.IsUserTurn && tool.Category != AgentToolCategory.Observation) || mutates && !context.AllowSideEffects)
            return Finish(ToolRunStatus.ActivityDenied, "当前活动不允许执行该工具。");
        if (policy.Sensitivity == AgentToolSensitivity.ScreenContent ||
            policy.Sensitivity == AgentToolSensitivity.ActivityMetadata && !context.AllowActivityMetadata ||
            !mutates && tool.Category != AgentToolCategory.Observation && !context.AllowReadTools)
            return Finish(ToolRunStatus.Unauthorized, "未获得该类数据的读取授权。");
        if (policy.RequiresExplicitIntent && string.IsNullOrWhiteSpace(context.UserRequest))
            return Finish(ToolRunStatus.Unauthorized, "需要用户请求。");
        if (mutates || policy.RequiresConfirmation)
        {
            if (approve is null) return Finish(ToolRunStatus.ConfirmationRequired, "尚未确认，未执行；宿主需要显示具体操作供用户批准。");
            try
            {
                // Only the host's human approval can grant authority for this exact mutation.
                var approved = await approve(new(request.Name, tool.DescribeAction(request.Arguments),
                    argumentsJson, hash, policy.Effect, context.UserRequest), ct).ConfigureAwait(false);
                if (!approved) return Finish(ToolRunStatus.Declined, "用户拒绝，未执行。");
            }
            catch (OperationCanceledException) { return Finish(ToolRunStatus.Cancelled, "确认已取消，未执行。"); }
            catch (Exception ex)
            {
                (diagnostics ?? LocalDiagnosticLog.Default).Write("tool.approval", ex);
                return Finish(ToolRunStatus.Unauthorized, "确认服务不可用，未执行。");
            }
        }
        if (ct.IsCancellationRequested) return Finish(ToolRunStatus.Cancelled, "已取消，未执行。");
        if (!budget.TryReserve()) return Finish(ToolRunStatus.BudgetExceeded, "工具调用预算已用尽，未执行。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1,
            Math.Min(Math.Clamp(policy.TimeoutSeconds, 1, 600) * 1000, budget.Remaining.TotalMilliseconds))));
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var error = tool.ValidateArguments(request.Arguments);
            if (error is not null) return Finish(ToolRunStatus.InvalidArguments, error);
            effect = mutates ? ToolEffectState.Unknown : ToolEffectState.None;
            var input = tool.UsesJsonArguments ? argumentsJson :
                request.Arguments.TryGetProperty("input", out var text) ? text.GetString() : null;
            var execution = Task.Run(() => tool.ExecuteAsync(input, deadline.Token), deadline.Token);
            _ = execution.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var output = await execution.WaitAsync(deadline.Token).ConfigureAwait(false);
            effect = mutates ? ToolEffectState.Completed : ToolEffectState.None;
            var limit = Math.Min(Math.Clamp(policy.MaxOutputCharacters, 1, 16000), budget.OutputRemaining);
            var truncated = output.Length > limit;
            output = output[..Math.Min(output.Length, limit)];
            budget.Consume(output.Length);
            return Finish(ToolRunStatus.Succeeded, output, truncated);
        }
        catch (OperationCanceledException)
        { return Finish(ct.IsCancellationRequested ? ToolRunStatus.Cancelled : ToolRunStatus.TimedOut,
            mutates && effect == ToolEffectState.Unknown ? "执行中断，可能已有部分副作用，请核查输出。" : "执行已中断。"); }
        catch (Exception ex)
        {
            (diagnostics ?? LocalDiagnosticLog.Default).Write("tool.execute", ex);
            return Finish(ToolRunStatus.Failed, ex.GetType().Name +
                (effect == ToolEffectState.Unknown ? "：可能已有部分副作用。" : "：工具未完成。"));
        }
    }
}
