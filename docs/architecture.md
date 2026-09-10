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
- `IAgentPromptBuilder`：集中拼装人设、隐私、原声、RAG 和文档朗读约束；
- `ISpeechSegmentParser`：解析隐藏情绪/原声标签，并执行原声严格校验；
- `ICharacterSpeechSynthesizer`：选择情绪参考并调用 TTS；
- `SpeechText`：统一 UI 与 Core 对括号动作的判定，保证动作不发声。

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
agent/src/HuTao.Agent.Core/
├── Abstractions/      # 跨层接口
├── Configuration/     # 角色和运行配置
├── Core/              # ReactAgent、调度器等 Agent 领域编排
├── DocumentReading/   # 文档朗读领域模型与应用服务
├── Llm/               # DeepSeek / Mock 实现
├── Persona/           # 人设和原声目录
├── Rag/               # 剧情知识库
├── Runtime/           # AgentRuntimeFactory 组合根
├── Storage/           # 聊天记录、重要记忆
├── Tools/             # Agent 工具适配器
└── Tts/               # GPT-SoVITS 与情绪参考实现
```

WPF 的 `CharacterThemeCatalog` 只保存视觉颜色，不保存 persona、语音或文件路径；这些统一来自 Core 的 `CharacterCatalog`。窗口交互按职责拆在 `MainWindow.xaml.cs`、`MainWindow.CharacterSelection.cs` 和 `MainWindow.DocumentReading.cs`，后续可继续把音频播放和主动调度拆成独立适配器。
