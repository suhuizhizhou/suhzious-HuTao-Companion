# 声线克隆管线（GPT-SoVITS v3）· Phase 2 实操文档

> 目标：用 **GPT-SoVITS v3** 做 **few-shot 基线**——输入「胡桃参考音频 + 目标文本」，输出「胡桃声线音频」，
> 并逐步演进到**实时语音预测**（低延迟、流式）。
>
> 官方仓库：https://github.com/RVC-Boss/GPT-SoVITS
> 中文 README：`docs/cn/README.md`（模型清单/下载地址以它为准）

## 1. 前置条件

| 项 | 要求 |
| --- | --- |
| Python | 3.10–3.12（建议 3.11） |
| GPU | NVIDIA + CUDA（推荐 ≥8GB 显存）；纯 CPU 可跑但慢，不适合"实时" |
| FFmpeg | 需在 PATH 中（音频解码/编码用） |
| 磁盘 | 预训练模型约 3–5GB |

## 2. 获取 GPT-SoVITS v3

```powershell
git clone https://github.com/RVC-Boss/GPT-SoVITS.git
Set-Location GPT-SoVITS
python -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements.txt
```

**下载预训练模型**（v3，如 `s2Gv3D` 等，文件名随版本变动）：

- 放置到 `GPT_SoVITS/pretrained_models/`；
- 中文前端 `G2PWModel` 放置到 `GPT_SoVITS/text/G2PWModel/`；
- 具体清单与下载地址以官方中文 README 为准（HuggingFace / ModelScope 均有镜像）。

## 3. 数据准备（few-shot 不需要训练）

few-shot 只需**参考音频 + 参考音频对应文本**，无需微调。挑选原则：

- 无 BGM、无混响、单人、清晰；
- 每条 **3–10 秒**为宜；
- **覆盖不同情绪/语气**（活泼、认真、俏皮……），提升音色与语气还原；
- 每条都有**准确文字标签**（即 prompt text）。

使用 `scripts/prepare_dataset.py` 从 `data/voice/` 的 manifest 生成 GPT-SoVITS 可用的参考音频清单。

## 4. 启动推理服务

```powershell
Set-Location GPT-SoVITS
.\.venv\Scripts\python api_v2.py -a 127.0.0.1 -p 9880
```

- 打开 http://127.0.0.1:9880/docs 查看**确切参数**（v3 字段以它为准）。
- 常用启动参数：`-a` 绑定地址、`-p` 端口、`-c` 配置文件。

## 5. few-shot 推理调用

使用 `voice/infer/client.py`（封装了下面的请求）。

**核心请求字段**（基于 v2 稳定接口，v3 以 `/docs` 为准）：

| 字段 | 含义 | 示例 |
| --- | --- | --- |
| `text` | 要合成的目标文本 | "大丘丘病了，二丘丘瞧……" |
| `text_lang` | 目标语言 | `zh` / `en` / `ja` |
| `ref_audio_path` | 参考音频路径（胡桃原声） | `data/voice/raw/hutao_0001.wav` |
| `prompt_text` | 参考音频对应文字 | 该音频的标签文本 |
| `prompt_lang` | 参考音频语言 | `zh` |
| `top_k` / `top_p` / `temperature` | GPT 采样参数 | `5` / `1.0` / `1.0` |
| `text_split_method` | 文本切句方式 | `cut0` ~ `cut5` |
| `batch_size` | 批量 | `1` |
| `speed_factor` | 语速 | `1.0` |
| `fragment_interval` | 分句静音间隔（秒） | `0.3` |
| `seed` | 随机种子（-1 随机） | `-1` |
| `media_type` | 输出格式 | `wav` / `mp3` |
| `streaming_mode` | 流式输出（v3） | `false` / `true` |

## 6. 实时 / 流式化路径

few-shot 基线先跑通「文本 → 完整音频」，再评估实时化：

1. **v3 自带流式**：`streaming_mode: true` 返回分块音频，首包延迟更低；
2. **Aqua-TTS**（第三方低延迟 GPU 推理运行时，针对 streaming GPT-SoVITS v3 优化）：https://github.com/Lucas1479/Aqua-TTS
3. 最终接入 `bridge/`：C# 把 Agent 产出的台词发给推理服务，分块音频经 `bridge/` 回传前端播放。

## 7. 评估（`voice/eval/`）

| 指标 | 说明 |
| --- | --- |
| 音色相似度 | 说话人相似度（如 speaker embedding 余弦）或主观 A/B |
| RTF（实时率） | 合成时长 / 音频时长，< 1 才可能实时 |
| 首包延迟 | 从请求到第一段音频的时间，目标接近对话延迟 |
| 自然度 MOS | 主观评分（1–5） |

## 8. 验收口径（Phase 2 完成标准）

- [ ] 用 1–5 分钟干净胡桃参考音频，合成出可辨识的胡桃声线；
- [ ] 单句 RTF 达标（GPU 下 < 1，并记录实测值）；
- [ ] 中文文本正常合成、无乱码、切句合理；
- [ ] 推理服务可被 `voice/infer/client.py` 稳定调用；
- [ ] 输出参数/结果记录进 `notes/decisions.md`。

## 9. 常见问题

- **中文合成乱码/异常**：确认 `G2PWModel` 已就位，`text_lang` 为 `zh`。
- **`/tts` 404**：v3 的 api_v2.py 端点可能变化，打开 `/docs` 核对端点与字段。
- **OOM**：降低 `batch_size`、缩短参考音频、检查显存。
- **FFmpeg 缺失**：`ffmpeg -version` 验证，或安装后加入 PATH。
