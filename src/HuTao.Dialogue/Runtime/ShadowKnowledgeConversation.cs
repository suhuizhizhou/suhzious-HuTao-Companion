using HuTao.Dialogue.Core;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Runtime;

/// <summary>One authoritative conversation; deterministic shadow retrieval has no second model, memory writer or reply.</summary>
internal sealed class ShadowKnowledgeConversation(IAgentConversation primary,KnowledgeRuntime knowledge) : IAgentConversation
{
    public IReadOnlyList<ChatMessage> History=>primary.History;
    public ConversationPrivacyStamp? PrivacyStamp=>primary.PrivacyStamp;
    public async Task<AgentTurnResult> RespondAsync(string userInput,CancellationToken ct=default)
    {var result=await primary.RespondAsync(userInput,ct).ConfigureAwait(false);await Shadow(userInput,ct).ConfigureAwait(false);return result;}
    public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct=default)=>primary.ProactiveAsync(ct);
    public async Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(string? userInput,bool isProactive,CancellationToken ct=default)
    {var result=await primary.GenerateSpeechSegmentsAsync(userInput,isProactive,ct).ConfigureAwait(false);if(!isProactive)await Shadow(userInput,ct).ConfigureAwait(false);return result;}
    private async Task Shadow(string? input,CancellationToken ct)
    {try{await knowledge.ObserveShadowAsync(input,ct).ConfigureAwait(false);}catch(Exception) when(!ct.IsCancellationRequested) { /* Shadow availability does not change the authoritative reply. */ }}
    public Task<TtsResult?> SynthesizeAsync(string text,string emotion="neutral",double intensity=0.5,CancellationToken ct=default)=>primary.SynthesizeAsync(text,emotion,intensity,ct);
    public void RestoreHistory(IEnumerable<ChatMessage> history)=>primary.RestoreHistory(history);
    public void RestoreHistory(IEnumerable<ChatMessage> history,ConversationPrivacyStamp? privacy)=>primary.RestoreHistory(history,privacy);
    // 必须转发：本类是显式实现接口的装饰器，不转发就会吃到默认实现（返回 null），
    // 于是 Shadow 模式下切会话拿不到任何上下文，历史会被静默清空。
    public ConversationContext? CaptureContext()=>primary.CaptureContext();
}
