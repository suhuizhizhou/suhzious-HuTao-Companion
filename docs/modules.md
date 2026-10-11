# 模块维护地图

本文件是阅读与修改代码的入口，不代表所有预留接口已经有产品实现。

## 1. 从用户操作往下读

```text
WPF ChatShellWindow / 控制台 Host
  → ChatShellViewModel、ChatSessionRuntime（会话、轮次与取消）
  → AgentRuntimeFactory / AgentPool（装配与实例生命周期）
  → ReactAgent（单角色回合入口）
      → 工具观察 / ConversationRecall / ReAct 检索
      → 提示构造、回复生成、事实与沉浸审查、受控重试
      → 最终文本结果
  → SpeechDeliverySession（桌面逐段语音投递）
  → UI 气泡与音频播放
```

控制台整轮调用另经 `ReactAgent.Speech.cs` 做语音适配。两条交付路径不能因为名字相似就随意合并：桌面端需要逐段投递、取消和队列生命周期。

## 2. 依赖方向与所有权

| 层 | 目录 | 应负责 | 不应放入 |
| --- | --- | --- | --- |
| 公共契约 | `src/HuTao.Foundation` | 接口、消息契约、诊断、环境定位 | WPF、具体模型与数据库实现 |
| 进程桥接 | `src/HuTao.Bridge` | Python 定位、子进程启动与通信基础 | 角色行为、业务提示词 |
| 语音 | `src/HuTao.Voice` | TTS 实现、参考音频配置、推理生命周期 | 会话历史、UI 控件 |
| 人设 | `src/HuTao.Persona` | 角色配置、词表、原声素材与召回 | 宿主窗口状态、数据库恢复 |
| 知识 | `src/HuTao.Knowledge` | RAG、记忆、SQL 权威存储、图投影、文档朗读 | WPF、桌面会话按钮逻辑 |
| 对话 | `src/HuTao.Dialogue` | 会话、工具权限、回合编排、运行时装配 | XAML、Python 模型实现细节 |
| 宿主 | `hosts` | 输入输出、生命周期、确认与展示 | 自行绕过工具授权或重复实现检索 |

依赖检查采用 Foundation → Bridge → Voice/Persona → Knowledge → Dialogue → Hosts 的层级顺序；引用只能指向更底层。检查同时覆盖源码命名空间与项目引用，见 `DependencyDirectionChecks`。

## 3. 回合代码在哪里

- `ReactAgent.cs`：依赖注入、公共入口、回合互斥、历史与隐私有效性边界、共用辅助方法。
- `ReactAgent.Turn.cs`：取证、生成、审查、重试以及最终历史写回。
- `ReactAgent.Speech.cs`：整轮文本到语音的交付适配，保留合成前后隐私检查。
- `ResponseRepairPolicy.cs`：无 I/O 的重试约束策略；可直接输入输出测试，不需要 LLM。

前三者是同一个 `partial` 类型，并非三个独立服务。文件拆分降低阅读成本；纯策略提取才形成了可独立测试的边界。没有为每个方法再增加一层接口。

回合中的事实、隐私与取消判断不能交给 UI 补救。一个结果生成完成，不代表它在切换会话、遗忘或权限变更后仍然可以投递。

## 4. 聊天窗口阅读顺序

| 文件 | 职责 |
| --- | --- |
| `ChatShellWindow.xaml.cs` | 字段、构造、启动与运行时创建、窗口公共入口 |
| `ChatShellWindow.Rendering.cs` | 会话列表、消息、标题与上下文展示 |
| `ChatShellWindow.Interaction.cs` | 输入、会话切换、菜单、群组管理事件 |
| `ChatShellWindow.Presentation.cs` | 音频、临时状态、窗口布局与偏好 |
| `ChatShellWindow.Lifecycle.cs` | 关闭请求、等待活动任务、清理与关闭失败处理 |
| `ChatShell/RenameSessionDialog.cs` | 独立重命名对话框 |

以上文件均位于 `hosts/HuTao.Pet`。窗口仍然通过 `ChatShellViewModel` 使用会话功能，拆文件没有改变 Dispatcher 线程归属。`_startup`、`_activeTurn`、`_audioGate`、`_pendingCleanup` 的生命周期关系和名称保留。

## 5. SQL、图和记忆不是三份同等权威的数据

SQL 模式下，SQL 原文、权限与版本是权威；检索结果必须回到有权限、版本有效的原文证据。Neo4j 是可重建投影，不是跳过 SQL 权限检查的第二条读通道。

- 原文与证据读取：`Knowledge/Storage`、`Knowledge/Retrieval`。
- 提炼与图投影：`Knowledge/Graph`，通过作业、发布与 outbox 分离生命周期。
- 记忆检索与持久化适配：`Knowledge/Memory`。
- 启动模式、authority fence、回滚装配：`Dialogue/Runtime`。

修改数据结构先明确：权威数据在哪里、变更是否需要迁移、旧证据何时失效、失败后如何恢复。不要为了缩短文件把事务边界和投递前隐私复查移到不同的无序后台任务。

## 6. 新能力应该放在哪里

| 新需求 | 首选扩展位置 | 必要回归 |
| --- | --- | --- |
| 新语音模型 | Voice 中的引擎适配 + TTS 工厂 | 失败保留文本、动作静音、取消与隐私失效 |
| 新角色 | Persona 配置与素材；宿主主题适配 | 角色隔离、台词与音频一致性 |
| 新 Agent 工具 | Dialogue/Tools + 装配入口 | 参数、权限、确认、超时与输出限制 |
| 新检索策略 | Knowledge 的检索边界 | 证据原文、命名空间隔离、预算与降级 |
| 新会话交互 | Sessions / ViewModel / 窗口相应职责文件 | 切换取消、投影、清理、播放顺序 |
| 新持久化模式 | 明确 repository 契约及迁移流程 | 原子写回、旧版本兼容、恢复与隐私 |

## 7. 检查入口

在仓库根目录执行：

```powershell
dotnet build HuTao.Companion.sln --no-restore -m:1 -p:UseSharedCompilation=false
dotnet tests/HuTao.StoryRag.Eval/bin/Debug/net9.0/HuTao.StoryRag.Eval.dll --offline-checks
```

首次检出尚无 NuGet 还原资产时，先 `dotnet restore HuTao.Companion.sln`；还原依赖可能需要联网。

统一入口覆盖 SQL、工具、召回与记忆、对话、会话、语音、群聊和依赖方向检查。使用临时测试数据与 Stub，不调用真实 DeepSeek、TTS 或外部图服务。不读取完整生产剧情库，也不声称替代真实质量评估。

也可保留原来的模块参数，或一次选几个模块：

```powershell
dotnet tests/HuTao.StoryRag.Eval/bin/Debug/net9.0/HuTao.StoryRag.Eval.dll --conversation-checks --speech-checks
```

其他可选参数：`--knowledge-sql-checks`、`--tool-checks`、`--recall-checks`、`--session-checks`、`--chatroom-checks`。

退出码：0 全部通过；1 有失败；2 有跳过且没有失败。离线模块参数不可与真实评估参数混用，防止跑了离线检查却误以为完成线上评估。

完整语料结构评估、角色资源检查、真实 API 评估、真实 Neo4j 和 WPF 生命周期运行仍是不同验证层。它们有不同前置条件，不应被一个“全绿”数字掩盖。
