# 素材与第三方组件声明（NOTICE）

本文件说明本仓库中**不受 MIT 许可覆盖**的内容。授权范围见 [LICENSE](LICENSE)。

## 1. 不属于本许可范围的素材

| 内容 | 权利归属 | 仓库处理 |
| --- | --- | --- |
| 《原神》角色形象、台词、剧情文本、语音音频 | 米哈游（miHoYo / HoYoverse） | 不入库（见 `.gitignore`） |
| 《异环》角色形象、剧情文本、语音音频 | 完美世界等相关权利人 | 不入库 |
| 由公开资料整理的人设提炼（语录、口头禅、设定提要） | 原权利人 | `data/persona/*/quotes.json`、`catchphrases.json`、`lore.md` 等少量文本保留，仅用于角色扮演演示 |
| 游戏解包语料、语音数据集、模型权重 | 原权利人 | 不入库，由使用者本地自行生成 |

**本地生成这些内容的脚本是作者原创代码（受 MIT 覆盖），但生成出来的产物不是。**
脚本清单与用法见 [docs/story-rag.md](docs/story-rag.md)（语料导入）与
[docs/voice-pipeline.md](docs/voice-pipeline.md)（声线数据）。

`data/story/lexicon.json` 是作者整理的实体与同义词名单（不是解包文本），受 MIT 覆盖。

## 2. 第三方组件

| 组件 | 用途 | 说明 |
| --- | --- | --- |
| [GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS) | few-shot 语音克隆推理 | 以 git clone 方式使用，**未修改上游代码**；许可以官方仓库为准 |
| [vgmstream](https://github.com/vgmstream/vgmstream) | 游戏音频（Wwise `.wem` 等）解码 | 以二进制形式在本地使用，`tools/` 不入库 |
| FFmpeg | 文档朗读的音频合并与转码 | 许可随所选构建而定（LGPL 或 GPL），以官方发布为准 |
| [BAAI/bge-small-zh-v1.5](https://huggingface.co/BAAI/bge-small-zh-v1.5) | 可选的语义检索服务 | 需自行下载；许可以模型页为准 |
| .NET 9 / WPF / ASP.NET 无关依赖 | 应用运行时 | MIT |

本项目的 C# 侧**不引用任何第三方 NuGet 包**（只用 BCL），因此不存在传递依赖的授权问题。

## 3. 使用边界

1. 本仓库仅用于**个人学习与技术演示**，不得用于任何商业用途。
2. 不得分发、售卖从游戏中提取的音频、文本、模型权重及其衍生内容。
3. 不得使用本项目或其产出冒充官方内容，或让角色自称官方。
4. 若你在本地生成并使用了受版权保护的素材，请自行确认所在地区的合规要求，风险自负。

## 4. 若你是权利人

若你认为本仓库中的任何内容侵犯了你的权益，请通过仓库 Issue 联系，我们会尽快移除相关内容。
