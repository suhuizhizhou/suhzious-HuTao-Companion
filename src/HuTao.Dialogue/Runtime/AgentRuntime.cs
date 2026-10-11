using HuTao.Foundation.Abstractions;
using HuTao.Persona;
using HuTao.Dialogue.Core;
using HuTao.Knowledge.Memory;
using HuTao.Dialogue.Tools;
using HuTao.Voice;

namespace HuTao.Dialogue.Runtime;

/// <summary>一次角色运行时装配结果。宿主只持有此对象，不需要了解内部依赖图。</summary>
public sealed record AgentRuntime(
    CharacterDefinition Character,
    PersonaProfile Persona,
    IAgentConversation Agent,
    DocumentReadingTool DocumentReader,
    ITtsEngine? Tts,
    EmotionReferenceCatalog EmotionReferences,
    OriginalVoiceCatalog OriginalVoices,
    IReadOnlyList<IAgentTool> Tools,
    /// <summary>长期记忆维护入口；宿主在空闲时调用，不占用对话链路。</summary>
    MemoryMaintenance? Memory = null,
    ConversationMemoryStore? MemoryStore = null,
    KnowledgeRuntime? Knowledge = null,
    IDisposable? LegacyLifetime = null,
    IDisposable? SqlLifetime = null,
    /// <summary>
    /// 创建这个运行时用的记忆作用域。QQNT 会话列表里它等于会话 Id
    /// （<c>single_hutao</c> / <c>group_default</c> / <c>group_20260214T201533-ab12</c>），
    /// 桌宠窗口沿用 <c>desktop</c>。
    ///
    /// 暴露它是为了让 <c>AgentPool</c> 能**断言**"每个会话各自的记忆文件互不相同"
    /// （设计书不变量 I3）——这条不变量是"两个 store 覆写同一份 json"那类事故的唯一防线，
    /// 不能只靠调用方自觉。
    /// </summary>
    string MemoryScope = "desktop") : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        Exception? memoryFailure = null;
        try
        {
            if(LegacyLifetime is not null && MemoryStore is not null)
                MemoryStore.FlushAndRetire(LegacyLifetime.Dispose);
            else
            {
                MemoryStore?.Flush(throwOnError:true);
                LegacyLifetime?.Dispose();
            }
        }
        catch (Exception ex) { memoryFailure = ex; }

        try
        {
            if (Knowledge is not null) await Knowledge.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (memoryFailure is not null)
        {
            throw new AggregateException("Memory persistence and knowledge cleanup both failed.", memoryFailure, cleanupFailure);
        }

        if (memoryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(memoryFailure).Throw();
        SqlLifetime?.Dispose();
    }
}
