# 胡桃 AI 桌宠（HuTao Companion）

一个会**用胡桃的声音主动跟你说话**的 AI 桌宠：使用《原神》角色「胡桃」的声线，扮演她的人设，以 ReAct Agent 的方式感知你在做什么，站在桌面上随时跟你搭话。

> 仅限个人学习与技术演示。胡桃形象、声线、台词版权归米哈游（miHoYo / HoYoverse）。

## 已实现功能

- **声线**：用 GPT-SoVITS v3 few-shot 适应胡桃声线，输入文字输出胡桃语音。
- **人设**：提炼的胡桃人设，希望能够通过dsv4生成符合人设的台词。
- **对话**：主动搭话；不理她会发「小别扭」
- **记忆区**：聊天记录持久化，重开可重放、可清空；
- **桌面桌宠**：WPF 透明置顶窗口，气泡、语音同出，语音可重播
- **ReAct Agent**：Reason → Observe → Think → Act，调用工具感知环境（活动窗口、空闲状态、时间）

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
├── docs/                    # 部署日志
├── data/
│   ├── persona/             # 胡桃人设 Skill 
│   └── voice/               # 语音数据（.gitignore，需自行提取，见下）
├── voice/
│   ├── infer/few_shot_infer.py   # GPT-SoVITS 按需推理脚本
│   ├── infer/resident_server.py  # GPT-SoVITS 常驻 HTTP 服务
│   ├── requirements.txt
│   └── GPT-SoVITS-main/          # 第三方项目（.gitignore，需自行 clone）
├── agent/                   # C# Agent + WPF 桌宠
│   ├── src/HuTao.Agent.Core/     # 核心库
│   ├── src/HuTao.Agent.Host/     # 控制台演示
│   ├── src/HuTao.Pet/            # WPF 桌宠
│   ├── build.ps1 / run.ps1 / run-pet.ps1
└── scripts/                 # 数据提取/整理脚本
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
```

### 4. 构建并运行桌宠

```powershell
cd agent
.\build.ps1     # 构建
.\run-pet.ps1   # 启动
```

启动后：一个置顶小窗口，气泡「本堂主来啦！」；输入框打字回车与她对话；放着不动 10 分钟以上她会主动搭话。

> 只想看 Agent 后端（无 GUI），可跑控制台演示：`.\run.ps1`

## 文档导航

| 文档 | 内容 |
| --- | --- |
| [`docs/voice-pipeline.md`](docs/voice-pipeline.md) | 声线克隆管线实操 |
| [`docs/persona-skill.md`](docs/persona-skill.md) | 人设 skill 提取规格 |

## 合规声明

本项目仅用于**个人学习与技术演示**，不用于任何商业用途。胡桃的角色形象、配音、台词文本版权归米哈游（miHoYo/HoYoverse）所有。请勿传播、售卖或商业利用从游戏中提取的音频、模型权重及衍生内容。
