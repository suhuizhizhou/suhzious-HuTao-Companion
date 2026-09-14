# 项目结构与扩展边界

项目按“领域模型—应用服务—基础设施—宿主适配器”组织。目标是让新增文档格式、TTS 后端、Agent 工具或 UI 功能时，只替换对应边界，不把实现细节继续堆到 `ReactAgent` 或 `MainWindow.xaml.cs`。

## 运行时装配

```text
CharacterCatalog
       │
       ▼
AgentRuntimeFactory ── PersonaLoader / LLM / Tools / TTS
       │
       ├── ReactAgent
       └── DocumentReadingTool ── IDocumentReadingService
```

`AgentRuntimeFactory` 是角色运行时的唯一装配入口。命令行 Host 和 WPF 使用同一套工具、人设、原声、记忆和文档朗读配置；WPF 只额外负责主题和交互状态。`TtsRuntimeFactory` 统一创建常驻/按需 GPT-SoVITS，运行时音频写入 `data/voice/generated/`，不再污染角色原始素材目录。未来的云端或流式引擎仍通过 `ITtsEngine` 注入。

`ReactAgent` 只维护对话历史并编排 ReAct 顺序，具体职责拆为：

- `IToolObservationCollector`：工具执行与观察结果格式化，可继续增加并行、超时和审计；
- `IAgentPromptBuilder`：集中拼装人设、沉浸约束、隐私、分层原声、RAG 和文档朗读约束；
- `ISpeechSegmentParser`：解析隐藏情绪/原声标签，并执行原声严格校验；
- `ICharacterSpeechSynthesizer`：选择情绪参考并调用 TTS；
- `OriginalVoiceRetriever`：用整轮上下文做三路原声召回，产出候选但不做最终裁决；
- `ImmersionGate`：两层出戏闸门，决定最终对用户可见的台词；
- `SpeechText`：统一 UI 与 Core 对括号动作的判定，保证动作不发声。

## 沉浸性与原声的边界

```text
ReactAgent
   ├── OriginalVoiceRetriever ── OriginalVoiceCatalog
   │      └── 只产出「候选」；能否播放由 SpeechSegmentParser 逐字校验裁决
   ├── MemoryRetriever ── ConversationMemoryStore ── MemoryBm25Index
   │      └── 只产出「召回片段」；写入与失效由 Store 负责，提炼由 Consolidator 负责
   └── ImmersionGate
          ├── ImmersionRuleGate   # 确定性、零延迟，机械性出戏
          └── ImmersionCritic     # LLM 评审，语义性出戏与跨轮连贯（含长期记忆）
```

- `ImmersionGate` 的输出是**唯一**对用户可见的台词来源。它内部可以重写、可以退兜底，但绝不把违规内容向上传递。
- 审查员在**一次性构造的历史**上工作，审查过程与指令绝不写回 `_history`，因此评测本身不会破坏角色上下文连贯。
- 审查员同时拿到本轮召回给演员的**同一份长期记忆**，所以长程自相矛盾（比最近 8 条更早的事实）也能被发现。
- `OriginalVoiceCatalog` 是只读素材目录；`OriginalVoiceRetriever` 是检索策略。换检索算法不需要动目录，换素材不需要动算法。
- 记忆四层职责分离：`ConversationMemoryStore` 管「记什么 / 何时失效」，`MemoryBm25Index` 管索引，`MemoryRetriever` 管打分与渲染，`MemoryConsolidator` 管提炼。后台提炼由 `MemoryMaintenance` 按空闲/待处理量/冷却调度，**宿主只需在已有的空闲轮询里调用它**。
- RAG 侧的原声通道收在 `StoryAnswerComposer` 的一个可选 `voice_id` 字段里，白名单由 `RagExact` 候选集合决定，越权 id 静默丢弃。

详见 [沉浸性保障](immersion.md)、[长期记忆](memory.md) 与 [原声优先](original-voice.md)。


## 面向后续能力的稳定契约

- `IAgentConversation`：传输层只依赖会话接口，未来 REST/WebSocket 不需要直接引用 `ReactAgent`；
- `IAgentEventSink`：发布 Reason/Observe/Think/Act 和音频事件，UI、日志、WebSocket 或调试回放都可以旁路订阅；
- `IStreamingTtsEngine`：为首包播放、实时语音和嘴型同步预留流式分块接口，当前非流式引擎无需改造；
- `ISpeechRecognitionEngine`：为本地/远程 ASR 预留输入边界；
- `ICharacterActionSink`：用与 SDK 无关的 Expression/Motion/LipSync/Notification 协议承接未来 MMD、Live2D 或 VRM；
- `AgentToolPolicy`：工具声明只读、活动元数据、外部变更等边界，为后续审批、并行调度和 MCP schema 提供元数据。

这些接口是契约，不代表当前版本已经实现对应外部服务；实现新增能力时优先新增适配器，避免把协议、模型或渲染 SDK 侵入 Core 编排。

## 文档朗读 pipeline

```text
文档路径
  → IDocumentTextExtractor       # TXT / DOCX；未来可加 EPUB、Markdown、字幕
  → IReadingPlanner              # 标题、段落、句子、长度和情绪阶段
  → IReadingEmotionSelector      # 情绪 → 参考音频与推理参数
  → ITtsEngine                   # 每段 WAV；由引擎决定常驻/按需
  → IReadingJobStore             # source.txt / plan.json / segments/
  → IAudioSegmentMerger          # FFmpeg MP3；未来可替换其他编码器
```

当前默认实现位于 `Core/DocumentReading/`：

- `DocumentTextExtractor`：格式解析，不包含 TTS 逻辑；
- `ReadingPlanBuilder`：确定性切分和关键词情绪判断，可替换为 LLM 规划器；
- `ReadingEmotionSelector`：复用角色情绪参考库；
- `FileReadingJobStore`：保存可回放的中间产物；
- `FfmpegAudioSegmentMerger`：只负责 FFmpeg 进程和编码参数；
- `DocumentReadingService`：按顺序编排上述依赖并统一处理取消、失败和进度；
- `DocumentReadingTool`：只负责 Agent 触发词和路径提取，是最薄的一层。

未来可以沿接口扩展：

| 需求 | 扩展点 |
| --- | --- |
| EPUB、HTML、SRT | 新增 `IDocumentTextExtractor` 实现 |
| 语义章节切分、说话人识别 | 新增 `IReadingPlanner` 实现 |
| 角色/段落更细的情绪策略 | 新增 `IReadingEmotionSelector` 实现 |
| 暂停、续读、失败重试 | 扩展 `IReadingJobStore` 保存状态和检查点 |
| AAC、OGG、无损 WAV | 新增 `IAudioSegmentMerger` 实现 |
| 远程 TTS、流式 TTS | 新增 `ITtsEngine` 实现，不改朗读服务 |
| 进度条、后台队列、历史任务列表 | 在 WPF/其他宿主增加 UI 适配器，不改 Core pipeline |

## 目录约定

### 剧情服务边界

StoryKnowledgeTool 仅适配 Agent 工具；IStoryRagService 管理查询、共享快照、预算和缓存；StoryCorpus 管理来源；StoryLineIndex 负责 BM25/RRF/合法窗口；IStorySemanticSearch 是可选本机 Dense 边界；StoryAnswerComposer 独立负责短回复协议与结构校验。

ReactAgent 每轮只调用一次剧情服务，并把隐藏证据保存在 TextResult/AgentTurnResult，避免与 TTS 或 WPF 耦合。详见 [Story RAG 白盒文档](story-rag.md) 与 [测试规范](story-rag-benchmark.md)。

```text
src/                          # C# 库，按依赖方向分层（箭头只能向下，见 docs/modules.md）
├── HuTao.Foundation/         # 零依赖：跨层接口 Abstractions、诊断、仓库定位
├── HuTao.Bridge/             # C# ↔ Python：便携运行时定位与子进程启动
├── HuTao.Voice/              # TTS 引擎（常驻/按需/兜底）+ 情绪参考
├── HuTao.Persona/            # 人设包、角色词表、原声目录与召回、角色配置
├── HuTao.Knowledge/          # 剧情 RAG、长期记忆、文档朗读
└── HuTao.Dialogue/           # ReactAgent、沉浸闸门、聊天室、工具、存储、LLM 实现
    ├── Core/                 #   ReactAgent、调度器等领域编排
    ├── Immersion/            #   出戏规则闸门、LLM 评审与编排管线
    ├── ChatRoom/             #   多人聊天室：轮次、导演接口、LLM 导演
    ├── Tools/                #   工具适配器（含 CLI 包装与 MCP 适配）
    ├── Storage/              #   聊天记录、重要记忆
    ├── Llm/                  #   DeepSeek / Mock 实现
    └── Runtime/              #   AgentRuntimeFactory 组合根
hosts/                        # 可执行宿主：HuTao.Pet（WPF 桌宠）、HuTao.Host（控制台）
tests/                        # HuTao.StoryRag.Eval：结构检查 + 离线评测
```

> `CharacterCatalog` 现在在 `src/HuTao.Persona/Configuration`：它描述「有哪些角色、各自需要什么」，
> 属于角色包而非领域编排。**依赖方向由两条用例守着**（`dependency_direction_acyclic` 扫 `using`，
> `dependency_project_references_acyclic` 扫 `ProjectReference`），见 [`modules.md`](modules.md) §5。

WPF 的 `CharacterThemeCatalog` 只保存视觉颜色，不保存 persona、语音或文件路径；这些统一来自 Core 的 `CharacterCatalog`。窗口交互按职责拆在 `MainWindow.xaml.cs`、`MainWindow.CharacterSelection.cs`、`MainWindow.DocumentReading.cs` 和 `MainWindow.ChatRoom.cs`，后续可继续把音频播放和主动调度拆成独立适配器。聊天室窗口独立为 `ChatRoomWindow.xaml(.cs)`。

