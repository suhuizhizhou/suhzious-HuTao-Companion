using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Immersion;
using HuTao.Knowledge.Memory;
using HuTao.Persona;
using HuTao.Dialogue.Storage;
using HuTao.Voice;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Tools;
using System.Diagnostics;
using System.Text;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Core;

// 回合内部的取证、生成、审查和写回。取消/隐私边界仍由 ReactAgent 入口统一守护。
public sealed partial class ReactAgent
{
    private async Task<TextResult> GenerateTextTurnAsync(string? userInput, bool isProactive, string turnId, CancellationToken ct,TurnTrace? trace)
    {
        ct.ThrowIfCancellationRequested();
        var reason = isProactive
            ? "主动搭话：心跳到点，且用户不忙、冷却已过"
            : $"回应用户输入：{userInput}";
        await PublishAsync(new AgentStageEvent(
            AgentStage.Reason,
            reason,
            DateTimeOffset.Now), ct).ConfigureAwait(false);

        var inputClock=Stopwatch.StartNew();
        trace?.Section("事项与用户事实写入");
        var memory = _importantMemory?.PreparePrompt(
            userInput,
            isProactive,
            DateTimeOffset.Now) ?? "";

        var forgetting=_importantMemory?.LastOperationWasForget==true;
        trace?.Key("事项处理",$"{inputClock.Elapsed.TotalMilliseconds:F0}ms");
        inputClock.Restart();
        if(forgetting)
        {
            _history.Clear();_turnPrivacy=_knowledge?.PrivacyStamp();_historyPrivacy=_turnPrivacy;
            userInput="请根据操作结果确认是否已删除指定事项，不复述事项内容。";
        }
        var now = DateTimeOffset.Now;
        string? committedUserId=null;
        if(!forgetting && userInput is not null && _knowledge is not null && _memoryStore is not null)
        {
            _memoryStore.CommitBatch(()=>
            {
                DemandTurnPrivacy();
                var userRecord=_memoryStore.ObserveTurn("user",userInput,now,deduplicate:false);
                committedUserId=userRecord?.Id;
                if(userRecord is not null && _importantMemory?.LastRememberedEvidenceId is { } importantSource)
                    userRecord.Sources.Add(importantSource);
            },ct);
            if(committedUserId is not null && _knowledge.IngestCommittedUserTurnAsync is { } ingest)
                await ingest(committedUserId,ct).ConfigureAwait(false);
        }
        trace?.Key("用户原文与事实写入",$"{inputClock.Elapsed.TotalMilliseconds:F0}ms");
        trace?.Section("工具观察");inputClock.Restart();
        var toolObservation = forgetting?"":await _observationCollector.CollectAsync(_ordinaryTools,userInput,ct).ConfigureAwait(false);
        trace?.Key("工具观察耗时",$"{inputClock.Elapsed.TotalMilliseconds:F0}ms");

        DemandTurnPrivacy();
        // 原声召回要看的是「本轮用户消息进来之前」的视角，所以先留一份快照，
        // 否则这一轮的用户消息会被重复算进上下文。
        var historyBeforeTurn = ConversationFocus.StartsNewTopic(userInput)?[]:ConversationFocus.ActiveHistory(_history);
        if (userInput is not null)
        {
            // History retains the previous-turn view; SQL may already contain this turn's explicit facts.
            _history.Add(new ChatMessage("user", userInput));
        }

        var generationHistory = ConversationFocus.ActiveHistory(_history);
        StoryRagResult? story = null;
        StoryAnswerResult? storyAnswer = null;
        ConversationSupportReview? conversationSupport=null;
        // 最后一轮的检索过程与沉浸判定结论随结果回传：在线评测要能看见「多级多次查询」
        // 与「闸门判过什么」，而不是只能看到一个被替换过的最终台词。
        ReactRetrievalResult? lastRetrieval = null;
        ConversationRecallResult? recall = null;
        IReadOnlyList<SpeechSegment> segments = [new SpeechSegment("……", "neutral", 0.3)];
        ImmersionVerdict? verdict = null;
        string? immersionPath = null;
        // 观察文本按轮次重建；保留最后一次尝试的版本给 TextResult。
        var observation = toolObservation;

        // 追踪由回合入口创建；这里仅计检索与生成阶段耗时，RAG 调用自动并入同一 trace。
        var stageClock = Stopwatch.StartNew();
        double composeMs = 0, gateMs = 0, planMs = 0;
        try
        {
        trace?.Section("输入");
        trace?.Key("用户", userInput ?? "（主动搭话，无用户输入）");
        trace?.Key("历史", $"{historyBeforeTurn.Length} 条");
        trace?.Key("记忆", memory.Length == 0 ? "无" : $"{memory.Length} 字");

        // ── 两层重试：先**生成级**（带证据、按闸门给的修改要求重做）→ 再**检索级**（换检索词重走 RAG）──
        //
        // 为什么必须分开：出戏的原因有两类，代价差一个数量级。
        //   · 表述问题（复读、语气、没接住问题）→ 同一份证据重做一次就够，一次 LLM 调用；
        //   · 证据本身不对（检索到系统/元信息口径的材料）→ 改措辞治不了根，必须重查，代价是一整套检索。
        // 旧实现把两者绑死：闸门内部先**无证据**地自由重写，重写完还不过才换检索词——
        // 于是最便宜的那条路反而绕开了证据与整套事实校验。
        //
        // 闸门在这里只出「修改要求」（ImmersionVerdict.Instructions），**重新生产由本方法负责**，
        // 因为只有它手里有本轮的证据。
        var constraints = new List<string>();
        var accepted = false;
        for (var retrievalAttempt = 0; !accepted; retrievalAttempt++)
        {
            // 主动搭话不做剧情检索：没有用户提问就没有可校验的问题，检索结果只会变成自我加戏。
            // 剧情断言必须由用户明确提问，走同一条检索 / 校验链。
            var retrievalQuery = retrievalAttempt == 0 ? userInput : ResponseRepairPolicy.ReviseRetrievalQuery(userInput, verdict);
            if (retrievalAttempt > 0)
            {
                trace?.Section($"检索级重试：换检索词重走 RAG（第 {retrievalAttempt} 次）");
                trace?.Key("新查询", retrievalQuery ?? "");
            }
            var planWatch = Stopwatch.StartNew();
            if (!forgetting && !isProactive && retrievalQuery is not null)
                recall = await _recall.RunAsync(retrievalQuery, historyBeforeTurn, now, ct,committedUserId).ConfigureAwait(false);
            var reactRetrieval = recall?.Story;
            planWatch.Stop();
            planMs += planWatch.Elapsed.TotalMilliseconds;
            story = reactRetrieval?.EvidencePool;
            lastRetrieval = reactRetrieval;
            observation = toolObservation;
            if (reactRetrieval is not null)
                observation += $"\n{reactRetrieval.Observation}";
            if (story is not null && story.Status != StoryStatus.Bypass)
                observation += $"\n- story_archive: {story.Status}; {story.Trace.RetrievalMode}; {story.Evidence.Count} evidence; {story.Trace.ElapsedMs:F0}ms";
            if (retrievalAttempt == 0)
                await PublishAsync(new AgentStageEvent(
                    AgentStage.Observe,
                    observation,
                    DateTimeOffset.Now), ct).ConfigureAwait(false);

            var longTermMemory = MemoryRetriever.FormatPromptSection(recall?.Memories ?? [], now, 1800);
            if (story is not null)
                story = story with { ConversationMemories = recall?.Memories ?? [] };

            // 原声召回用「整轮上下文」而不是「用户这一句话」：
            // 用户问「你和钟离什么关系」时，字面上跟任何一句台词都不重合，但本轮检索到的
            // 角色台词、近期对话合起来就能定位到本人录过的相关原声。
            var voiceCandidates = _voiceRetriever?.Retrieve(new OriginalVoiceQuery(
                userInput,
                CharacterEvidenceTexts(story),
                historyBeforeTurn.TakeLast(4).Select(message => message.Content).ToList(),
                // 用户在表达现实难过时不推台词库里的俏皮话，只保留本轮剧情逐字命中的原声。
                AllowLibrary: story?.Status != StoryStatus.Comfort)) ?? [];

            // 证据定稿后不再变——生成级重试复用它，只有检索级重试才会换掉它。
            var basePrompt = _promptBuilder.Build(new AgentPromptContext(
                _persona,
                observation,
                memory,
                voiceCandidates,
                _tools.ContainsKey("document_reader"),
                IsProactive: isProactive,
                LongTermMemory: longTermMemory,
                RecallIntent: recall?.Intent ?? "",
                GroundedConversation: _knowledge is not null));

            // 严格协议用尽后允许**松协议重做一次**：格式失败只该损失"可校验性"，
            // 不该损失整条回答——否则用户停在恒定兜底句上。
            var relaxed = false;
            for (var generationAttempt = 0; ; generationAttempt++)
            {
                // 松协议那一次要去掉「只返回 JSON / 绑定 evidence_ids」这类**格式类**要求：
                // 它们与"不要输出 JSON"直接矛盾，模型收到打架的指令只会给一句最保险的短回答（实测 17 字）。
                // 内容类要求（回应问题、不编造、口吻）必须保留。
                var constraint = ResponseRepairPolicy.BuildRetryConstraint(
                    constraints, userInput, isProactive, relaxed);
                var prompt = constraint.Length == 0 ? basePrompt : basePrompt + "\n\n" + constraint;

                storyAnswer = null;
                conversationSupport=null;
                IReadOnlyList<string> conversationIssues=[];
                IReadOnlyList<EvidenceReviewFinding> conversationFindings=[];
                string rawReply;
                var composeWatch = Stopwatch.StartNew();
                DemandTurnPrivacy();if(!_recall.KnowledgeIsCurrent(recall))throw new KnowledgeChangedException();
                if (recall?.ResponsePurpose is not (ConversationResponsePurpose.Comfort or ConversationResponsePurpose.Conversation) &&
                    story is not null && story.Status is not (StoryStatus.Bypass or StoryStatus.Playful or StoryStatus.Comfort))
                {
                    // 约束作为独立参数传入：Composer 内部拼成「人设 + 证据 JSON + 修改要求」，
                    // 保证重做时**证据和修改要求同时在场**。松协议那一次仍然带证据，只是不要 JSON。
                    storyAnswer = relaxed
                        ? await _storyComposer
                            .ComposeRelaxedAsync(_llm, basePrompt, generationHistory, story, voiceCandidates, constraint, ct)
                            .ConfigureAwait(false)
                        : await _storyComposer
                            .ComposeAsync(_llm, basePrompt, generationHistory, story, voiceCandidates, constraint, ct)
                            .ConfigureAwait(false);
                    rawReply = storyAnswer.Reply;
                }
                else
                {
                    var tone = (recall?.ResponsePurpose==ConversationResponsePurpose.Comfort?StoryStatus.Comfort:story?.Status) switch {
                        StoryStatus.Playful => "\n本轮是玩梗/假设。可以俏皮接话，明确这是想象，不声称官方发生过。",
                        StoryStatus.Comfort => "\n用户在表达现实的难过。温柔认真陪伴，不推销丧葬业务，不拿逝者开玩笑，不查剧情抢走话题。",
                        _ => "" };
                    rawReply = await _llm.CompleteAsync(prompt + tone, generationHistory, ct).ConfigureAwait(false);
                }
                composeWatch.Stop();
                DemandTurnPrivacy();if(!_recall.KnowledgeIsCurrent(recall))throw new KnowledgeChangedException();
                composeMs += composeWatch.Elapsed.TotalMilliseconds;

                // ── 一级修复：**本地确定性修复**（零 LLM）──
                // 结构 / 引用 / 格式类的契约问题（占实测失败的大头）本可以就地改好，
                // 不该占用生成级重试预算，更不该把整个回合拖进"重生成 → 换检索词 → 兜底"。
                // 这里只做确定性变换，修完内部会重新校验；修不动才交给下面重生成。
                if (storyAnswer is { Path: "validation-fallback", RawReply.Length: > 0 } && story is not null)
                {
                    var allowedVoices = voiceCandidates.Select(c => c.Clip.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var repaired = StoryAnswerComposer.TryLocalRepair(storyAnswer.RawReply, story, allowedVoices, _persona.Scripted);
                    if (repaired is not null)
                    {
                        trace?.Note($"本地确定性修复成功（原问题：{string.Join("、", storyAnswer.Issues)}）→ 不重生成");
                        storyAnswer = repaired;
                        rawReply = repaired.Reply;
                    }
                    else
                    {
                        trace?.Note($"本地修复无解（{string.Join("、", storyAnswer.Issues)}）→ 交给重生成");
                    }
                }

                if(story?.Knowledge is { } knowledgeBundle && storyAnswer is {Validated:true} && !storyAnswer.Path.Contains("fallback",StringComparison.Ordinal))
                {
                    var supportIssues=await EvidenceSemanticReviewer.ReviewAsync(_llm,storyAnswer,knowledgeBundle,ct,userInput??"").ConfigureAwait(false);
                    DemandTurnPrivacy();if(!_recall.KnowledgeIsCurrent(recall))throw new KnowledgeChangedException();
                    storyAnswer=supportIssues.Count==0?storyAnswer with {SemanticReview=storyAnswer.ValidationLevel==AnswerValidationLevel.Strict?"verbatim":"llm-supported"}:
                        StoryAnswerComposer.Fallback(story,supportIssues,"validation-fallback",_persona.Scripted) with {RawReply=storyAnswer.RawReply,SemanticReview="not-supported"};
                    rawReply=storyAnswer.Reply;
                }
                else if(_knowledge is not null && storyAnswer is null)
                {
                    var draftSegments=_segmentParser.Parse(rawReply);
                    trace?.Key("普通对话审查前草稿",rawReply);
                    var draft=new StoryAnswerResult(rawReply,draftSegments.Select(s=>new StoryAnswerSegment(s.Text,s.Emotion,[],"dialogue")).ToArray(),false,[],"conversation-draft");
                    var supportClock=Stopwatch.StartNew();
                    var supportReview=await EvidenceSemanticReviewer.ReviewDetailedAsync(_llm,draft,
                        recall?.Knowledge??new HuTao.Knowledge.Retrieval.EvidenceBundle([],[],new Dictionary<string,long>(),[]),ct,userInput??"",
                        new(_persona.Name,_persona.SystemPrompt+"\n"+_persona.Lore,generationHistory,observation+"\n"+memory)).ConfigureAwait(false);
                    conversationIssues=supportReview.Issues;conversationFindings=supportReview.Findings;
                    gateMs+=supportClock.Elapsed.TotalMilliseconds;
                    DemandTurnPrivacy();if(!_recall.KnowledgeIsCurrent(recall))throw new KnowledgeChangedException();
                    conversationSupport=new(conversationIssues.Count==0,conversationIssues,generationAttempt+1,draftSegments.Count);
                    _diagnostics.Turn(turnId,isProactive?"proactive":"user","conversation-support","llm-review",
                        conversationSupport.Passed?"reviewed":"rejected",segmentCount:draftSegments.Count,issues:conversationIssues);
                    trace?.Key("普通对话依据审查",conversationSupport.Passed?"模型审查通过（非严格事实证明）":string.Join("、",conversationIssues));
                    if(conversationIssues.Count>0)rawReply=await SafeFallbackAsync(ct).ConfigureAwait(false);
                }
                segments = _segmentParser.Parse(rawReply);
                if (segments.Count == 0)
                    segments = [new SpeechSegment("……", "neutral", 0.3)];

                // **每次生成尝试都留痕**：闸门判定**之前**的稿子 + 模型原始回包。
                // 只记最终答复会让"改了三次到底改了什么"完全不可见；
                // 而校验失败时 storyAnswer.Reply 已经是降级后的兜底话，
                // 不看原始回包就只能猜模型是不是压根没按协议回。
                trace?.Section($"生成尝试 {generationAttempt + 1}" + (relaxed ? "（松协议）" : ""));
                trace?.Key("草稿", storyAnswer?.Reply ?? rawReply);
                if (storyAnswer is not null)
                    trace?.Key("作答路径", storyAnswer.Issues.Count == 0
                        ? storyAnswer.Path
                        : $"{storyAnswer.Path} · 校验 {string.Join("、", storyAnswer.Issues)}");
                trace?.Key("原始回包", DescribeRawReply(storyAnswer?.RawReply));

                // Composer 自己降级过（模型挂了 / 契约校验没过）→ **这一版不算数**。
                // 为什么必须单独判：那句兜底话本身不出戏，闸门会放行，于是重试机制根本不会触发，
                // 用户就永远看到「唔，让我再理一理」——实测在线 100 例里有 62 例是这么来的。
                // 这里把 Composer 的校验问题也变成修改要求，交给同一份证据重做。
                var degraded = conversationIssues.Count>0 || storyAnswer is not null &&
                               storyAnswer.Path.Contains("fallback", StringComparison.Ordinal);

                ImmersionOutcome? outcome = null;
                if (_immersion is not null && degraded)
                {
                    // 契约已经判定这一版不可用，闸门再去评**降级后的兜底句**没有任何信息量：
                    // 那句恒定台词当然接不住用户问题（实测评审员给 0.05 —— 判得没错，只是评错了对象），
                    // 还白花一次评审调用，并往下一轮约束里塞进与内容无关的要求。
                    trace?.Key("闸门", "未运行（Composer 已按契约降级，评那句兜底话没有意义）");
                }
                else if (_immersion is not null)
                {
                    // 沉浸判定：聊天窗口里绝不允许出现任何出戏内容。
                    // 审查用的历史就是演员本轮看到的那段（含刚加入的用户消息），
                    // 评审员在临时历史上工作，审查过程不会写回 _history。
                    var gateWatch = Stopwatch.StartNew();
                    outcome = await _immersion.ReviewAsync(new ImmersionRequest(
                        _persona.Name,
                        prompt,
                        rawReply,
                        segments,
                        generationHistory,
                        userInput,
                        isProactive,
                        await SafeFallbackAsync(ct).ConfigureAwait(false),
                        longTermMemory), ct).ConfigureAwait(false);
                    gateWatch.Stop();
                    gateMs += gateWatch.Elapsed.TotalMilliseconds;
                    segments = outcome.Segments;
                    verdict = outcome.Verdict;
                    var path = generationAttempt == 0 ? outcome.Path : $"{outcome.Path}/gen-retry-{generationAttempt}";
                    immersionPath = retrievalAttempt == 0 ? path : $"{path}/observe-retry-{retrievalAttempt}";
                    // 只记违规类型，不记任何文本片段——日志不得含模型正文或用户输入。
                    _diagnostics.Turn(Guid.NewGuid().ToString("N"), isProactive ? "proactive" : "user",
                        "immersion", immersionPath,
                        outcome.Verdict.KindsSummary, segmentCount: segments.Count,
                        issues: outcome.Verdict.Violations.Select(v => v.Kind.ToString()).Distinct().ToArray());
                    trace?.Key("闸门", $"{immersionPath} · {outcome.Verdict.KindsSummary} · 连贯 {outcome.Verdict.Coherence:F2}");
                }

                var passed = outcome?.Verdict.Passed ?? true;
                if (passed && !degraded) { accepted = true; break; }

                var blocking = outcome is not null &&
                               outcome.Verdict.Violations.Any(v => ImmersionGate.IsBlocking(v.Kind));
                if (generationAttempt >= _immersionOptions.MaxGenerationAttempts)
                {
                    // 严格协议用尽。若这次失败是"模型没按格式回"（validation-fallback）而不是出戏，
                    // 再给一次**松协议**机会：仍然带同一份证据，只是不要 JSON。
                    // 实测这条路径上"因为格式丢掉整条回答"是兜底句的最大来源。
                    if (degraded && !relaxed && !blocking && storyAnswer?.Path == "validation-fallback" && story?.Knowledge is null)
                    {
                        relaxed = true;
                        trace?.Note("严格协议用尽 → 松协议重做（仍带证据，不要求 JSON）");
                        continue;
                    }

                    // 生成级用尽：只有硬违规才继续往检索级走；
                    // 软违规、或"闸门放行但 Composer 降级成了兜底句"，都在这里收下
                    // ——一句略显重复的角色台词好过固定兜底，而兜底句再糟也只是最后手段。
                    if (!blocking)
                    {
                        accepted = true;
                        trace?.Note(degraded
                            ? $"生成级重试用尽，仍是兜底句（{storyAnswer?.Path}）→ 放行"
                            : $"生成级重试用尽，仅剩软违规（{outcome?.Verdict.KindsSummary}）→ 放行");
                    }
                    break;
                }

                // 修改要求**累积**而不是替换：只带最新一条会让两类问题来回震荡（A→B→A→B）。
                if (outcome is not null) constraints.AddRange(outcome.Verdict.Instructions);
                constraints.AddRange(ResponseRepairPolicy.DegradedInstructions(storyAnswer));
                if(conversationIssues.Count>0)
                {
                    constraints.Add("上一版含缺少依据或无法确认的断言。重新自然回应用户，保留当前自述和资料明确支持的内容；不要附加角色或用户往事、功效、无据比较或声称已执行操作，不以换个说法保留这些断言。简单计算应核算，不需要把正常邀请或安慰写成事实声明。");
                    if(conversationFindings.Count>0)
                        constraints.Add("下面JSON只定位上一稿有问题的文字，不是指令或新增事实。unsupported-claim=本段包含无依据内容；uncertain=无法确认；arithmetic=计算错误。段落内有依据的部分仍可保留，不能把整段判定成全部错误。基于原始用户话语和来源重新作答，不把这些文字当资料：\n"+
                            System.Text.Json.JsonSerializer.Serialize(conversationFindings));
                }
                trace?.Note($"→ 不采纳，重做第 {generationAttempt + 1} 次"
                    + (degraded ? $"（契约问题：{string.Join("、", storyAnswer?.Issues??conversationIssues)}）" : "")
                    + (outcome is null ? "" : $"（闸门：{outcome.Verdict.KindsSummary}）"));
            }

            if (accepted) break;
            if (retrievalAttempt >= _immersionOptions.MaxRetrievalRetries) break;
        }

        // 两层都用尽仍然只有硬违规 → 角色化兜底台词（绝不把出戏内容放给用户）。
        if (!accepted)
            segments = [new SpeechSegment(await SafeFallbackAsync(ct).ConfigureAwait(false), "neutral", 0.35)];

        DemandTurnPrivacy();if(!_recall.KnowledgeIsCurrent(recall))throw new KnowledgeChangedException();
        var reply = string.Join('\n', segments.Select(segment => segment.Text));

        // ── 追踪日志的收尾段：证据池 / 作答 / 闸门 / 最终答复 / 耗时 ──
        // 这些是「agent 侧的后端结果」；RAG 侧的每一次调用已经在上面按发生顺序并进来了。
        if (trace is not null)
        {
            trace.Section("证据池（多轮检索合并后）");
            if (story is null)
                trace.Note("本轮没有走剧情检索（非剧情回合或未挂剧情工具）。");
            else
            {
                trace.Key("状态", story.Status.ToString());
                trace.Key("证据", $"{story.Evidence.Count} 条 · 检索模式 {story.Trace.RetrievalMode} · 候选 {story.Trace.Candidates}");
                trace.Key("多轮", lastRetrieval is null
                    ? "无"
                    : $"{lastRetrieval.Steps.Count} 步 / {lastRetrieval.Steps.Select(s => s.Query).Distinct().Count()} 条不同查询 / 补查 {lastRetrieval.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal))} 次");
                foreach (var warning in story.Trace.Warnings.Take(6))
                    trace.Note("⚠ " + warning);
            }

            trace.Section("作答（知识库契约）");
            if (storyAnswer is null)
                trace.Note("本轮不走剧情回答协议（旁路/玩梗/安慰），直接由演员生成。");
            else
            {
                // `Fallback` 返回的 `Validated` 是 true（兜底句本身是安全的），
                // 所以只印它会出现「路径=validation-fallback 且 校验=通过」这种自相矛盾的两行。
                // 日志里按**读的人关心的事**说：这一轮到底有没有按契约作答。
                var degraded = storyAnswer.Path.Contains("fallback", StringComparison.Ordinal);
                trace.Key("路径", storyAnswer.Path);
                trace.Key("验证等级",storyAnswer.ValidationLevel.ToString());
                trace.Key("校验", degraded ? "未通过 → 已降级为兜底句"
                    : storyAnswer.Validated ? "通过" : "未通过");
                if (storyAnswer.Issues.Count > 0)
                    // 同一个问题会在多段上各报一次；按名去重计数，否则一行里能出现六遍 unsupported_number。
                    trace.Key("问题", string.Join("、", storyAnswer.Issues
                        .GroupBy(issue => issue, StringComparer.Ordinal)
                        .Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key)));
                trace.Key("草稿", storyAnswer.Reply);
            }

            trace.Section("沉浸闸门");
            if (verdict is null)
                trace.Note("闸门未接线（关闭或未启用）。");
            else
            {
                trace.Key("路径", immersionPath ?? "-");
                trace.Key("通过", verdict.Passed ? "是" : "否");
                trace.Key("违规", verdict.KindsSummary);
                trace.Key("连贯", verdict.Coherence.ToString("F2"));
                trace.Key("来源", verdict.Source);
            }

            trace.Section("最终答复（用户可见）");
            trace.Line(reply);
        }

        _history.Add(new ChatMessage("assistant", reply));
        // 角色说过的话同样进记忆库：她自己的承诺与说法也是长程一致性的依据。
        if(!forgetting && _knowledge is not null && _memoryStore is not null)_memoryStore.CommitBatch(()=>
        {
            DemandTurnPrivacy();
            var assistantRecord=_memoryStore.ObserveTurn("assistant",reply,now,deduplicate:false);
            if(assistantRecord is not null && committedUserId is not null)assistantRecord.Sources.Add(committedUserId);
            if(assistantRecord is not null && recall?.Knowledge is { } usedKnowledge)
                foreach(var source in usedKnowledge.Evidence.Where(e=>e.Reference.Namespace==_knowledge.Access.MemoryNamespace && e.Reference.Id!=assistantRecord.Id))
                {
                    if(!assistantRecord.Sources.Contains(source.Reference.Id))assistantRecord.Sources.Add(source.Reference.Id);
                    assistantRecord.SourceRevisions[source.Reference.Id]=source.Reference.Revision;
                }
        });
        else if(!forgetting)
        {
            if(userInput is not null)_memoryStore?.ObserveTurn("user",userInput,now);
            _memoryStore?.ObserveTurn("assistant",reply,now);
        }
        await PublishAsync(new AgentStageEvent(
            AgentStage.Think,
            reply,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        return new TextResult(reason, observation, reply, segments)
            { Story = story, StoryAnswer = storyAnswer, ConversationSupport=conversationSupport, Immersion = verdict, ImmersionPath = immersionPath, Retrieval = lastRetrieval, Recall = recall,
                ToolRuns = (_observationCollector as ToolObservationCollector)?.LastResults ?? [] };
        }
        finally
        {
            // **必须在 finally 里收尾**：回合中途抛异常时，前面已经发生的检索仍然要留下记录——
            // 出问题时最想看的恰恰是「走到哪一步炸的」，半途丢弃等于把最有用的那次运行丢掉。
            stageClock.Stop();
            var total = stageClock.Elapsed.TotalMilliseconds;
            if (trace is not null)
            {
                if(_runtimeIsCurrent?.Invoke()==false || (_turnPrivacy is not null && _knowledge?.PrivacyIsCurrent(_turnPrivacy)!=true))trace.Discard();
                trace.Section("耗时");
                trace.Key("检索生成阶段合计", $"{total:F0}ms");
                trace.Key("编排", $"{planMs:F0}ms（规划 + 多轮检索）");
                trace.Key("起草", $"{composeMs:F0}ms");
                trace.Key("闸门", $"{gateMs:F0}ms");
                trace.Key("其它", $"{Math.Max(0, total - planMs - composeMs - gateMs):F0}ms");
            }
        }
    }

    /// <summary>
    /// 只取本轮证据里**本角色本人**说过的台词。
    /// 必须按说话人过滤：剧情语料里「什么？！」这类短句被多个角色共用，
    /// 不过滤就会把别人的台词错配成本角色的原声。
    /// </summary>
}
