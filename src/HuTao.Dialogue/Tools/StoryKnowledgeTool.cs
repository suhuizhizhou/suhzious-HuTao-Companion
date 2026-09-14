using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Rag;

namespace HuTao.Dialogue.Tools;

/// <summary>Agent 适配层；检索逻辑、会话分析、回答校验均位于独立 RAG 服务。</summary>
public sealed class StoryKnowledgeTool : IAgentTool
{
    public IStoryRagService Service { get; }

    public StoryKnowledgeTool(IStoryRagService service) => Service = service;

    public StoryKnowledgeTool(StoryVectorStore store, StoryDialogueStore? dialogues = null)
        : this(new StoryRagService(dialogues?.RootPath ?? Path.Combine(Path.GetTempPath(), "missing-story-corpus"), store)) { }

    public string Name => "story_archive";

    /// <summary>
    /// 说明由**角色名 + 作品名**拼出，不再是写死的「胡桃…提瓦特…」。
    /// 两个名字都取自词表（角色词表给名字，语料词表给作品），所以新增角色/作品不用改这行。
    /// </summary>
    public string Description
    {
        get
        {
            var lexicon = Service is StoryRagService rag ? rag.Lexicon : null;
            var self = string.IsNullOrWhiteSpace(lexicon?.SelfName) ? "这个角色" : lexicon!.SelfName;
            var work = string.IsNullOrWhiteSpace(lexicon?.WorkName) ? "作品" : lexicon!.WorkName;
            return $"回忆{self}自己的故事，或翻阅有出处的{work}台词与经历。";
        }
    }

    /// <summary>
    /// 检索。<paramref name="strategy"/> 与 <paramref name="chapterScope"/> 都透传给服务层，
    /// 让**每次调用**都能指定臂与两级召回的第二级章节。
    /// 这是「agent 按轮次换臂 / 按需扩章节」在工具层的落点：服务层已支持，而工具层此前
    /// 只转发 (input, history, ct)，导致 ReAct 循环**物理上无法换臂**。
    /// </summary>
    public Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null,
        CancellationToken ct = default, RetrievalStrategy? strategy = null,
        IReadOnlyList<string>? chapterScope = null) =>
        Service.RetrieveAsync(input, history, ct, strategy, chapterScope);

    public async Task<string> ExecuteAsync(string? input = null, CancellationToken ct = default)
    {
        var result = await RetrieveAsync(input ?? "", ct: ct).ConfigureAwait(false);
        return result.Status == StoryStatus.Bypass ? "未触发（当前对话不是剧情查询）。" : StoryAnswerComposer.Prompt(result);
    }
}
