using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Rag;

namespace HuTao.Agent.Core.Tools;

/// <summary>Agent 适配层；检索逻辑、会话分析、回答校验均位于独立 RAG 服务。</summary>
public sealed class StoryKnowledgeTool : IAgentTool
{
    public IStoryRagService Service { get; }
    public StoryKnowledgeTool(IStoryRagService service) => Service = service;
    public StoryKnowledgeTool(StoryVectorStore store, StoryDialogueStore? dialogues = null)
        : this(new StoryRagService(dialogues?.RootPath ?? Path.Combine(Path.GetTempPath(), "missing-story-corpus"), store)) { }

    public string Name => "story_archive";
    public string Description => "回忆胡桃自己的故事，或翻阅有出处的提瓦特台词与经历。";
    public Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null,
        CancellationToken ct = default) => Service.RetrieveAsync(input, history, ct);

    public async Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        var result = await RetrieveAsync(input ?? "", ct: ct).ConfigureAwait(false);
        return result.Status == StoryStatus.Bypass ? "未触发（当前对话不是剧情查询）。" : StoryAnswerComposer.Prompt(result);
    }
}
