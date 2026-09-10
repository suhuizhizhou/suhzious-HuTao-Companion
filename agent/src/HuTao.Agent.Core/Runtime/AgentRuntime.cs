using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Configuration;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Persona;
using HuTao.Agent.Core.Tools;
using HuTao.Agent.Core.Tts;

namespace HuTao.Agent.Core.Runtime;

/// <summary>一次角色运行时装配结果。宿主只持有此对象，不需要了解内部依赖图。</summary>
public sealed record AgentRuntime(
    CharacterDefinition Character,
    PersonaProfile Persona,
    IAgentConversation Agent,
    DocumentReadingTool DocumentReader,
    ITtsEngine? Tts,
    EmotionReferenceCatalog EmotionReferences,
    OriginalVoiceCatalog OriginalVoices,
    IReadOnlyList<IAgentTool> Tools);
