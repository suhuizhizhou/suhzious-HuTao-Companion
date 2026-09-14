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
    ConversationMemoryStore? MemoryStore = null);
