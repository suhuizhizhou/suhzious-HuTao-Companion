# 胡桃 AI 桌宠（HuTao Companion）

一个会**用胡桃的声音主动跟你说话**的 AI 桌宠：使用《原神》角色「胡桃」的声线，扮演她的人设，以 ReAct Agent 的方式感知你在做什么，站在桌面上随时跟你搭话。

> 仅限个人学习与技术演示。胡桃形象、声线、台词版权归米哈游（miHoYo / HoYoverse）。

## 已实现功能

- **声线**：用 GPT-SoVITS v3 few-shot 适应胡桃声线，输入文字输出胡桃语音。
- **人设**：提炼的胡桃人设，希望能够通过dsv4生成符合人设的台词。
- **对话**：主动搭话（走 LLM，结合你正在做什么找话题）；不理她会发「小别扭」
- **记忆区**：聊天记录持久化，重开可重放、可清空；
- **长期记忆**：零成本的 BM25 历史检索 + 双时态时间有效性 + 空闲后台提炼离散事实，角色记得住往事也知道哪些已过时
- **桌面桌宠**：WPF 透明置顶窗口，支持完整聊天、头像气泡、完全隐藏三种模式，语音可重播
- **ReAct Agent**：Reason → Observe → Think → Act，调用工具感知环境（前台应用与窗口标题、空闲状态、时间）
- **沉浸闸门**：两层出戏防线，聊天窗口里不出现任何 AI 自述、客服腔、系统概念或格式泄露；评审员同时看长期记忆，长程自相矛盾也会被拦下
- **剧情 RAG**：自然问句定位逐句原文，支持胡桃个人故事、相邻追问、证据引用和可选本机 BGE 语义检索；资料缺口不会自动补写。
- **召回策略交给 agent**：六条**具名**召回臂（精确词法 / 概念扩展 / 语义探索 / 章节先验 / **两级扩章节** / 融合）由 agent 在规划阶段**逐个问题点名**，多级多次查询由它自己编排（含按缺口补差与跨轮检索记忆）；臂只能在有限命名集合里选，不接受连续权重
- **可复盘**：默认写一份**可读的后端日志**（agent + RAG 一轮到底检索了什么、召回了哪几行、闸门改没改、最后答了什么），另有离线臂矩阵与在线整回合评测——两者按各自的分层标准判定，不统一口径
- **原声优先**：剧情回答可直接引用角色原声，召回由整轮上下文驱动而非用户单句
- **多人聊天室**：多个角色同场对话，发言顺序由独立导演调度；支持跨作品组局（原神 × 异环），每个角色用自己的剧情语料。一位角色的模型暂时不可用时会自动换下一位，不会让整间聊天室散场
- **多角色**：胡桃、芙宁娜、可莉（原神）+ 安魂曲、塔吉多（异环）。异环角色的人设、剧情语料与原声由独立的 C# 提取工具链从本机游戏资源生成，产出规范与桌宠完全一致

剧情服务的源码阅读路线、数据结构、算法和启动方式见 [Story RAG 白盒文档](docs/story-rag.md)；[测试规范](docs/story-rag-benchmark.md)区分离线检索指标与真实回答/角色自然度，运行入口见 [评测说明](evaluation/story-rag/README.md)。

## 技术栈

| 层 | 技术 |
| --- | --- |
| 语音克隆 | Python · GPT-SoVITS v3（few-shot） |
| 大模型 | DeepSeek API |
| 主程序 | C# / .NET 9 |
| 桥接 | C# 通过进程调用 Python 推理脚本 |

## 架构

```text
┌─────────────────────────────────────────────┐
│  WPF 桌宠（透明置顶窗口、气泡、语音重播）     
└───────────────────┬─────────────────────────┘
                    │
┌───────────────────▼─────────────────────────┐
│  ReactAgent（Reason→Observe→Think→Act）    
│  ├ 人设 Skill    
│  ├ 工具             
│  ├ ProactiveScheduler  
│  └ ChatLogStore（记忆区）      
└───────┬──────────────────┬──────────────────┘
        │ ILLMProvider     │ ITtsEngine
        ▼                  ▼
   DeepSeek API     GPT-SoVITS v3（Python）
```

## 目录结构

```text
hutao-companion/
├── HuTao.Companion.sln         # 解决方案（9 个项目，0 警告 0 错误）
├── src/                        # 六个库，按依赖方向分层（只能向下引用）
│   ├── HuTao.Foundation/       # 抽象、诊断日志、可读后端日志
│   ├── HuTao.Bridge/           # C# ↔ Python 桥接
│   ├── HuTao.Voice/            # TTS / 原声库
│   ├── HuTao.Persona/          # 人设包与词表
│   ├── HuTao.Knowledge/        # Story RAG、长期记忆、文档朗读
│   └── HuTao.Dialogue/         # ReactAgent、工具、沉浸闸门、聊天室
├── hosts/                      # 宿主（最上层）
│   ├── HuTao.Pet/              # WPF 桌宠
│   └── HuTao.Host/             # 控制台演示
├── tests/HuTao.StoryRag.Eval/  # 评测：结构检查 + 离线指标 + 在线回放
├── data/
│   ├── persona/                # 胡桃、芙宁娜、可莉……人设 Skill 与词表
│   ├── story/                  # 剧情语料与索引
│   └── voice/                  # 语音数据（.gitignore，需自行提取，见下）
├── voice/
│   ├── infer/few_shot_infer.py   # GPT-SoVITS 按需推理脚本
│   ├── infer/resident_server.py  # GPT-SoVITS 常驻 HTTP 服务
│   ├── requirements.txt
│   └── GPT-SoVITS-main/          # 第三方项目（.gitignore，需自行 clone）
├── docs/                       # 设计文档
└── scripts/                    # 构建、运行、数据提取与评测脚本
```

## 快速开始

### 0. 前置条件

- Windows 10/11，NVIDIA GPU（≥8GB 显存，用于 GPT-SoVITS 推理）
- conda（含 `pytorch` 环境：Python 3.12 + torch 2.3+cu121）
- .NET 9 SDK
- DeepSeek API Key

### 1. 搭语音环境（Python）

```powershell
# 复用 conda 的 pytorch 环境，在项目内建 venv
python -m venv --system-site-packages voice/.venv
voice/.venv/Scripts/python -m pip install -i https://mirrors.aliyun.com/pypi/simple/ -r voice/requirements.txt

# clone GPT-SoVITS 到 voice/GPT-SoVITS-main
git clone https://github.com/RVC-Boss/GPT-SoVITS.git voice/GPT-SoVITS-main
```

GPT-SoVITS 依赖与 v3 预训练模型下载，详见 [`docs/voice-pipeline.md`](docs/voice-pipeline.md)。

### 2. 准备胡桃语音数据

`data/voice/hutao/` 需要放胡桃的 wav + `manifest.jsonl`（台词标签）。获取方式见 [`docs/voice-pipeline.md`](docs/voice-pipeline.md) 与 [`scripts/extract_pck.mjs`](scripts/extract_pck.mjs)。仓库内已附标签格式示例 [`data/voice/labels/manifest.example.jsonl`](data/voice/labels/manifest.example.jsonl)。

### 3. 配置密钥

复制 `.env.example` 为 `.env`，填入：

```ini
DEEPSEEK_API_KEY=sk-你的key
# 可选：覆盖 TTS 的 python 路径
# HU_TAO_TTS_PYTHON=voice/.venv/Scripts/python.exe
# TTS 默认使用常驻服务，模型只加载一次；可改为 on-demand 逐句启动进程
# HU_TAO_TTS_MODE=resident
# HU_TAO_TTS_URL=http://127.0.0.1:9881/
# HU_TAO_TTS_DEVICE=cuda
# HU_TAO_TTS_HALF=true
# 前台应用感知默认开启；显式设为 false 才关闭
# HU_TAO_ALLOW_APP_AWARENESS=true
# 沉浸闸门默认两层全开；关掉评审层可省每轮一次调用
# HU_TAO_IMMERSION_CRITIC=false
```

### 4. 构建并运行桌宠

```powershell
.\scripts\build-agent.ps1   # 构建整个解决方案
.\scripts\run-pet.ps1       # 启动桌宠
```

启动后默认显示 `350 × 480` 的完整聊天窗口；标题栏 `◫` 可缩成头像与最近一条角色气泡，`—` 可完全隐藏到系统托盘。双击头像或托盘图标可恢复完整窗口，托盘右键菜单也可切换三种模式和“始终置顶”。窗口模式、位置及置顶开关保存在 `%LOCALAPPDATA%\HuTaoCompanion\ui-settings.json`。普通桌面窗口下使用 WPF `Topmost` 和 Windows 原生置顶双重保障；全屏独占程序和系统安全桌面仍可能覆盖桌宠。

默认进入胡桃；点击右上角角色按钮会展开**胡桃、芙宁娜、可莉、安魂曲、塔吉多**的选择列表，人设、声线、主题和聊天记录随角色切换。输入框打字回车即可对话；缺少音频时自动使用文字模式。

后两位来自《异环》（Neverness To Everness），人设包、剧情语料与原声全部由 `tools/nte` 从本机游戏资源提取，与桌宠既有规范完全一致——**跨作品只是多一条配置，检索与语音链路一条都没改**。安魂曲 450 条原声（363 MB / 72 分钟）、塔吉多 370 条，两人都能用真原声说话；他们的语音在游戏的**语言分包**（`TagPatchPaks`）里，工具需要多挂一个容器目录，见 [`tools/nte/README.md`](tools/nte/README.md) §2.5（`tools/` 不入库）。

标题栏 `💬` 打开**多人聊天室**：选好角色后，`▸` 让下一位接一句、`▶` 自动对谈、打字回车即插话（默认身份是旁观者）。每个角色走各自完整的 Agent 链路，所以聊天室里同样不出戏；导演的调度理由默认折叠在 `🐞` 里。设计与实测见 [`docs/chat-room.md`](docs/chat-room.md)。

标题栏 `◉`/`○` 切换**前台应用感知**，**默认开启**：只读当前前台那一个窗口的进程名与窗口标题，用于判断你此刻在做什么。不截图、不读文件、不枚举后台进程，关掉后不再读取任何窗口信息。系统提示（如开关状态）显示在输入框上方的独立状态行，不会混进角色气泡。

角色开场白直接播放本地游戏原声，不调用 LLM 或 TTS。正常聊天会用**整轮上下文**（你的问题 + 本轮检索到的剧情 + 近期对话）从本地完整语音清单中召回原声候选；Agent 只有在逐字选中候选台词时才直接播放对应 WAV，其余气泡继续使用 GPT-SoVITS 合成。剧情回答协议另有一个可选的 `voice_id` 通道，让检索到的角色原话直接以真原声播出。原理与实测见 [`docs/original-voice.md`](docs/original-voice.md)。

对外可见的每一句话都要先过**沉浸闸门**：确定性规则层零延迟拦截机械性出戏（自称 AI、客服腔、泄露系统概念、markdown、半角星号动作、复读），LLM 评审层在演员看过的同一段历史上补查语气一致性与跨轮连贯。开关与成本见 [`docs/immersion.md`](docs/immersion.md)。

右上角 `📖` 可选择 TXT 或 DOCX 生成角色声线朗读。系统按章节、段落和句子拆成可控长度，根据内容选择自然、开心、俏皮、关心、严肃或轻声参考音频，依次合成 WAV，最后调用项目自带 FFmpeg 合并为 192 kbps MP3。输出位于 `data/reading/<文档名_时间>/`，其中同时保留原文、分段计划和各段 WAV；也可在聊天中发送“朗读 D:\\path\\稿件.docx 并生成 MP3”调用同一工具。

Core 的分层边界和文档朗读扩展点见 [`docs/architecture.md`](docs/architecture.md)。

> 只想看 Agent 后端（无 GUI），可跑控制台演示：`.\scripts\run-agent.ps1`

## 开发与评测工具

| 目的 | 命令 |
| --- | --- |
| 构建 / 跑桌宠 / 跑控制台演示 | `scripts\build-agent.ps1` · `run-pet.ps1` · `run-agent.ps1` |
| 结构检查 + 离线指标（零 LLM 成本，`--strict` 作为门禁） | `dotnet run --project tests\HuTao.StoryRag.Eval -- --strict` |
| 召回臂离线矩阵：跑 7 个臂 → 每个臂在**它存在的层**上判定 | `scripts\run-arms.ps1` 然后 `scripts\strategy-report.ps1` |
| 在线整回合评测（真实 ReAct：检索 → 起草 → 沉浸判定 → 出答案） | `scripts\agent-online.ps1 -Limit 100` |
| 召回链路可视化演示页 | `python scripts\build-recall-demo.py` → `docs/recall-chain-demo.html`（生成物，不入库） |

**两种 observability 产物，分工不同**：

- **可读后端日志**（`BackendTrace`，默认开启）：agent + RAG 一轮到底做了什么——输入、检索编排与选中的臂、每次 RAG 调用的证据（含原文与分数）、证据池缺口、作答路径、沉浸闸门、最终答复、各阶段耗时。路径 `%LOCALAPPDATA%\HuTaoCompanion\logs\backend-trace.log`（宿主启动会打印），`HU_TAO_TRACE=off` 可关；格式见 [`docs/backend-trace-sample.log`](docs/backend-trace-sample.log)。
- **评测报告**：`--agent-live` / `--react-live` 会在输出目录落一份自描述的 `report.json`（含逐例轨迹、分层指标、判官结论），并记下可读日志的位置。

### 5. 构建本地 Release

```powershell
.\scripts\build_release.ps1
```

脚本默认构建完整语音版：除桌宠程序外，还会通过 `conda-pack` 打包便携 Python/PyTorch、GPT-SoVITS v3 与模型权重，并加入三角色参考/原声音频、FFmpeg 和剧情 RAG 数据。只有 `DEEPSEEK_API_KEY` 需要使用者在解压后的 `.env` 中填写。由于完整包超过 GitHub 单资产 2 GiB 限制，脚本会生成小于 1.9 GiB 的 `*.tar.gz.001` 分卷、`assemble-release.ps1` 和 SHA256 校验表；把所有分卷放在同一目录后执行组装脚本即可。

如只需要不含语音与剧情资源的文字版，可执行：

```powershell
.\scripts\build_release.ps1 -WithoutVoiceRuntime
```

完整包含游戏提取资源，只适合个人设备或获授权的私有分发，不应上传到公开 GitHub Release。

## 文档导航

| 文档 | 内容 |
| --- | --- |
| [`docs/architecture.md`](docs/architecture.md) | 项目结构与扩展边界 |
| [`docs/modules.md`](docs/modules.md) | 模块划分与解耦路线（目标形态 + 分步计划） |
| [`docs/data-flow.md`](docs/data-flow.md) | 数据通路解析（一次说话的全链路） |
| [`docs/story-rag.md`](docs/story-rag.md) | Story RAG：语料导入、检索链路、语义服务与评测口径 |
| [`docs/story-rag-benchmark.md`](docs/story-rag-benchmark.md) | 剧情问答基准集与标注规范 |
| [`docs/react-rag-evolution.md`](docs/react-rag-evolution.md) | ReAct × RAG 的演进：多级查询、召回臂与判官口径 |
| [`docs/immersion.md`](docs/immersion.md) | 沉浸性保障：两层出戏闸门的设计与取舍 |
| [`docs/memory.md`](docs/memory.md) | 长期记忆：检索、时间有效性、审查视野与后台整合 |
| [`docs/conversation-turn-policy.md`](docs/conversation-turn-policy.md) | 一轮对话的判定策略与降级路径 |
| [`docs/speech-failure-policy.md`](docs/speech-failure-policy.md) | 语音失败时的降级与兜底 |
| [`docs/chat-room.md`](docs/chat-room.md) | 多人聊天室与跨作品（原神 × 异环）剧情 RAG |
| [`docs/original-voice.md`](docs/original-voice.md) | 原声优先：让 RAG 真正驱动角色原声 |
| [`docs/journey.md`](docs/journey.md) | 从零到完成 · 项目总结 |
| [`docs/voice-pipeline.md`](docs/voice-pipeline.md) | 声线克隆管线实操 |
| [`docs/persona-skill.md`](docs/persona-skill.md) | 人设 skill 提取规格 |

## 合规声明

本项目仅用于**个人学习与技术演示**，不用于任何商业用途。胡桃的角色形象、配音、台词文本版权归米哈游（miHoYo/HoYoverse）所有。请勿传播、售卖或商业利用从游戏中提取的音频、模型权重及衍生内容。
