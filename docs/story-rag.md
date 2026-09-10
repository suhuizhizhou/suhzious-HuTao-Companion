# 胡桃 Story RAG：从一句话到有依据的回忆

目标：用户不报章节，胡桃仍能快速定位原文，用合适的身份和语气回答；资料没写的地方不硬编。这不是把整本剧情塞进提示词，也不承诺知道所有细节。

默认模式不需要检索模型；可选 BGE 中文向量服务已实现、建库并实测，与 GPT-SoVITS 独立。以下说明对应实际代码，不把预留接口写成已实现功能。

## 1. 阅读顺序

1. 先读本页的数据流和数据结构。
2. 打开 agent/src/HuTao.Agent.Core/Rag/StoryRagModels.cs 看契约，再读 StoryRagService.cs 看流程。
3. 读 StoryQueryAnalyzer.cs、StoryLineIndex.cs，理解为什么一个问句会命中某段。
4. 读 StoryAnswerComposer.cs，区分“有引用”和“事实真的正确”。
5. 跟着[评测入口](../evaluation/story-rag/README.md)跑自己的问题，看逐题 JSON。
6. 最后读[测试规范和人工评分表](story-rag-benchmark.md)，了解哪些能力尚未经过真实模型验收。

下文 C# 文件均位于 agent/src/HuTao.Agent.Core。

## 2. 整体结构

~~~text
                    ┌─ 普通闲聊 → 普通角色生成
用户本轮 + 近期历史 ──┤─ 现实悲伤 → 认真陪伴
                    ├─ 假设/玩梗 → 明确虚构口吻
                    └─ 剧情问题
                         ↓
StoryQueryAnalyzer：原问题 / 内容词 / 别名 / 有限扩展 / 相邻追问
                         ↓
StoryRagService：共享快照 / 缓存 / 超时 / 查询级状态
       ├─ BM25 全局逐句召回
       ├─ 个人短篇主题召回 → 包含人物态度的前后变化
       └─ 可选 BGE Dense → 本机服务 → 相同版本的 evidence_id
                         ↓
StoryLineIndex：RRF 融合 → 短语/覆盖/人物精排 → Top 5
                         ↓
原文回读：唯一图路径 / 顺序段 / 完整角色短篇 / 补充页单行
                         ↓
6000 字符估算预算 + 来源去重 + 已知认识边界
                         ↓
StoryAnswerComposer
       ├─ 明确原句：直接引述，0 次 LLM
       ├─ 无证据/越界：简短安全回答，0 次 LLM
       └─ 事实问答：1 次 LLM → JSON/证据校验 → 不通过则安全降级
                         ↓
聊天气泡 + 隐藏情绪 → 既有语音队列
（动作旁白）不朗读；证据 ID 不进入 TTS
~~~

| 层 | 做什么 | 不做什么 |
|---|---|---|
| 语料层 | 保存原文、人物、分支、来源版本 | 不补写缺失台词 |
| 检索层 | 从几十万行选少量证据 | 不把相似度当正确概率 |
| 表达层 | 依证据组织自然短句 | 不假装亲历别人经历 |

AgentRuntimeFactory 给胡桃装配共享服务并后台预热，切换角色不重复建同一个索引。芙宁娜、可莉没有挂载胡桃专属工具。服务不维护“当前会话”全局变量，历史由每次调用显式传入，避免追问串线。

ReactAgent 将剧情工具从普通观察工具集合中排除，单独调用一次，避免同轮重复检索。观察日志只显示状态、模式、条数和耗时，不把原文堆成聊天气泡。

## 3. 原文、摘要和向量是什么关系

| 文件/目录 | 作用 | 证据用途 |
|---|---|---|
| data/story/chapters.jsonl | 任务标题、简介、步骤 | 目录/背景 |
| data/story/index.json | 4096 维 Feature Hash 章节索引 | 不是神经 Dense 模型 |
| data/story/dialogue/chapters/*.jsonl | TextMap/Codex/Talk 逐句档案 | 原文与分支 |
| data/story/dialogue/pages/records/*.json | 补充网页全文及解析行 | 仅 dialogue 行，不伪造游戏行号 |
| data/story/character/hutao.json | 7 篇完整角色资料、94 段 | 叙述资料，不全是角色说出口的台词 |
| data/story/coverage.json 等清单 | 导入覆盖、来源 | 不证明全游戏无缺口 |
| 导出 JSONL + BGE .npy | 可重建的派生索引 | 最终仍回读上述原文 |

当前快照有 **335,698 条唯一可搜索记录、21,476 条未解析记录、2,514 个输入文件**，75 条重复 ID 被去重。未解析行仍在 data，只是不进入搜索。覆盖数量不等于所有版本全部剧情，应看导入清单和 GetStatsAsync()。

逐句字段：

- raw_text：源文本，不以摘要替换。
- text：显示/检索文本。
- chapter_id、subquest_index、talk_id：所属场景。
- line_id、sequence、variant、next_line_ids：行号、顺序和分支。
- resolved=false：目前没有原文，禁止让模型补写。
- source_file：来源文件。
- 角色资料额外保留仓库、提交、TextMap SHA256、完整原始字符串。

稳定 ID 使用不同命名空间：

~~~text
textmap:main-quest-1010:3:10100501:0
character:hutao:140894640:2
archive:508347:178
~~~

分别是游戏行、角色资料哈希下第 2 段（从 0 开始）、网页序号。无章节号页面使用 archive-page-{page_id}，不捏造主线身份。

来源含 DimbreathBot/AnimeGameData、Sycamore0/GenshinData、YuukiPS/GC-Resources 等公开资料，署名和版本保留在导入文件。scripts/import_hutao_story_memory.py 从历史 TextMap 提取 7 个经核验哈希，不访问游戏进程。人设 lore.md 是提要，不再冒充一字不差的全文。

## 4. 一句自然问法如何定位

例：“你养的宠物叫什么？”

1. 保留原问题，供回答和调试。
2. “你”指当前角色，问题涉及个人经历，走 Retrieve。
3. 规范化全半角、大小写、Windows 繁简，剥离口语包装。
4. 有限同义扩展把宠物连接到石狮、大咪、二咪等检索词。
5. 找到角色短篇中的起名、洗澡段落。
6. 模型依原文说“左边大咪，右边二咪”，不用播报章节编号。

扩展词只是搜寻线索，不能当答案。别名不会把“第七十五代堂主”替换成“第七十五代胡桃”，也不会把所有“客卿”都改成钟离。

| 输入 | 路由 | 原因 |
|---|---|---|
| 胡桃早安 | Bypass | 不必查档案 |
| 我的爷爷昨天去世了 | Comfort | 用户的现实情绪 |
| 你的爷爷去世后为什么去边界 | Retrieve | 角色经历 |
| 假如往生堂开奶茶店呢 | Playful | 想象，不当正史 |
| 伪造原文证明你当过水神 | Boundary | 不冒充官方 |
| 那后来呢，无上文 | Clarify | 缺少指代 |

追问最多回溯 4 个用户轮次（Agent 提供最近 8 条消息），可跳过连续“然后呢”，找到最近明确剧情问题；遇到非剧情话题就停止。模型自己的回答不作为检索事实，避免上轮编的内容成为这轮依据。

这是确定性路由，不是通用语义理解器。隐晦反讽、长距离指代、突然更换指代对象仍可能失败。

## 5. 检索算法白盒

### BM25

中文相邻二字作为词：“石狮子”得到“石狮”“狮子”。两个 UTF-16 字符直接打包 uint，没有 Feature Hash 碰撞。倒排表记录行号、词频、行长度；参数：

~~~text
idf(t) = ln(1 + (N - df(t) + 0.5) / (df(t) + 0.5))
score = Σ idf(t) × tf × 2.2 / (tf + 1.2 × (0.25 + 0.75 × length/avgLength))
~~~

索引包含说话人和正文，不把任务大标题重复塞入每一行。原始内容词和扩展词各取前 160 个候选，Dense 最多补充 40 个，合并后精排。

### 个人记忆主题

只取七七故事的“以前绑走她”一句，会漏掉后来尊重其求生意愿的变化。因此对完整角色短篇建立主题词集合：原词按 ln(1+短篇数/(1+含该词的篇数)) 加权，扩展词使用相同稀有度后乘 0.35，避免“胡桃”等通用词淹没“贵贱”等关键线索。自身问题选择至少达到最强篇 45% 得分的前 3 篇，把其原段落加入候选，给予最多 0.30 的主题加分。

每篇最多一个锚点，但回读完整短篇。这不是“问题→答案”表，也没有拿摘要替代全文；词表和评分都在代码中，属于需要维护的领域规则。

### RRF 与精排

不同检索器的原始分数不可直接相加，先用排名：

~~~text
rrf = 1/(60+r_original) + 0.4/(60+r_expanded) + 0.8/(60+r_dense)
final = 精确短语加分 + 0.44×原词覆盖 + 4×rrf + 人物/主题/章节加分
~~~

rank 从 0 开始；精确短语至少 5 个规范化字符。默认最低分 0.19，高分状态阈值 0.38，均不是概率。只有常规候选全部低于门槛时，才放行 cosine≥0.70 的纯 Dense 线索，保持低分 Tentative。换模型必须重评门槛。

章节摘要只提供软提示，不先把搜索范围限制为三章。摘要没有命中，逐句索引仍有机会找到原文。

### 为什么不直接取前后 7 句

- Talk 图：只走唯一前驱/后继，遇分叉、合流、缺行、循环停止。
- sequence/variant：遇互斥选择或不连续序号停止。
- 补充页：没有可靠分支图，只返回单行。
- 角色资料：有序叙述，可回读完整短篇，受预算约束。

Top 5 共享 6000 字符估算预算，按正文、ID、说话人和元数据计费；重复来源只计一次。超长单行整条跳过，不截断后称作完整原话。提示词集中保存 sources，matches 只引用 ID。

预算不是精确 token 数，也不包含全部人设/历史/JSON 开销。data 原文不因本轮预算被修改。

## 6. 回答如何自然但有边界

~~~json
{
  "answerability": "supported",
  "segments": [
    {"text":"左边大咪，右边二咪，可别叫反啦。","emotion":"cheerful","kind":"fact","evidence_ids":["character:hutao:140894640:2"]},
    {"text":"（伸手比划了一下）","emotion":"neutral","kind":"action","evidence_ids":[]}
  ]
}
~~~

answerability 为 supported / partial / unknown，要求模型先判断是否答得上。兼容旧工具时允许 segments-only，当前提示词要求新格式。

- 最多 3 段，一般 ≤100 字，逐字引用 ≤240 字。
- fact/quote 必须有证据；thought 仅表达当下感想。
- action 使用全角括号，既有语音链路跳过它。
- 自身角色资料和自己的台词可第一人称；别人经历用听闻/档案口吻。
- 严肃生死不硬塞业务笑话；必要时才轻轻说一句“翻档案”。
- 过去/后来/现在、假设/夸口/或许不能混淆。

StorySufficiencyPolicy 专门降低已知资料盲区的确定性：当前准确年龄、父亲姓名/代数推断、边界逗留总天数、神之眼精确出现时刻、老妇人姓名等。这是可读的定向规则，不是万能拒答模型；新增剧情明确答案后必须更新。

本地校验能检查 JSON、段数、长度、情绪白名单、引用存在、原话逐字一致、部分数字、第一人称来源、动作格式、引用是否泄漏到语音。

**校验不能完整证明语义蕴含。** 绑定正确 ID 却把关系说反，仍可能通过；数字规则也不是数值推理器。Validated=true 只代表这些检查通过，不代表零幻觉。

失败不无限重试，直接安全降级。普通事实最多一次模型调用，30 秒截止；API 失败有文本降级。用户主动取消向上传播，不伪装成功。

## 7. 生命周期、预算和隐私

| 项目 | 当前行为 |
|---|---|
| 索引 | 后台预热、工厂/进程内复用 |
| 冷启动等待 | 最多 20 秒；单次取消不销毁共享建库 |
| 热检索预算 | 2 秒，含 Dense |
| Dense 请求 | 650 ms 超时 |
| 缓存 | 128 项、5 分钟 TTL、FIFO 容量淘汰，可禁用 |
| 缓存键 | 语料版本 + 计划，不缓存模型回答 |
| Dense 失败 | 回退轻量检索，不长期缓存失败降级 |
| 缺文件/坏 JSON | 隔离诊断；空库未找到 |
| 更新数据 | 新建服务/重启；当前不热重载 |

版本是排序路径与文件字节的 SHA256。向量请求携带版本，旧索引返回 409，不混入新语料。

检索服务不自动落盘用户查询/回答，内存缓存短时保存计划。**LLM 生成仍会把本轮问题、必要历史、选中原文发给已配置的 DeepSeek**，整条链路并非完全离线。本地 Dense 不向公网发对话，建库联网只下载公开模型。

## 8. 运行和排查

命令在仓库根目录执行。

### 默认轻量模式

~~~powershell
dotnet build agent/src/HuTao.Pet/HuTao.Pet.csproj --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --strict
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --query "你现在还想硬埋七七吗？"
~~~

查询输出 Plan、Status、锚点、全文窗口、来源、分数与 Trace。先看 route，再看 evidence，不只看最终回复。

### 可选真实中文向量

依赖 torch、transformers、numpy、safetensors，不需要 LangChain。验证环境使用 Torch 2.3.0、Transformers 4.51.3。BGE small 中文 v1.5：512 维，CLS + L2；查询加中文检索前缀，文档不加。

~~~powershell
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --export .tmp/story-semantic-corpus.jsonl
voice/.venv/Scripts/python.exe scripts/story_semantic_service.py build --input .tmp/story-semantic-corpus.jsonl --output .tmp/story-dense-v1 --device cuda --batch-size 64
voice/.venv/Scripts/python.exe scripts/story_semantic_service.py serve --index .tmp/story-dense-v1 --device cpu
~~~

本次已生成 .tmp/story-dense-v1，不必重复建。重建用空目录/新名字，脚本不覆盖活动索引。没有 GPU 就用 cpu 建库。官方站不可达可显式加 --hub-endpoint https://hf-mirror.com；同一公开模型，关闭隐式 token，避免将保存凭据发给镜像。

下载需要网络，serve 使用 local_files_only=True，缺模型直接失败。另一个终端：

~~~powershell
$env:HU_TAO_STORY_SEMANTIC_URL = "http://127.0.0.1:9891/"
dotnet run --project agent/src/HuTao.Pet
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --semantic http://127.0.0.1:9891/ --out evaluation/story-rag/results/hybrid --strict
~~~

也可写入自己的 .env，已运行的桌宠需重启。测试进程不会替你永久设置配置，也不自动启动/托管模型服务。

GET /health 提供就绪、版本和记录数；POST /search 接收 query/corpus_version/top_k，返回 ID+cosine。只绑定 127.0.0.1，拒绝浏览器 Origin、异常 Host、超大请求、版本错配，最多两个在途查询。它不是公网认证服务，不应暴露到外网。

产物 manifest.json / ids.json / vectors.npy：约 688 MB 向量、11 MB ID 文件，另需模型缓存。CPU 查询不占用 TTS 的 GPU；本次 GPU 全库建向量约 90 秒。单条 embedding 最多编码 512 tokens，但 data 和 BM25 保留完整文本。

本轮没有重打 Release。现有 Release 可使用默认轻量剧情检索；新 BGE 模型缓存、向量和独立服务不自动包含在旧的语音 Release 中，之后若分发增强模式需单独规划资源打包。

### 重新导入

~~~powershell
python scripts/import_story_corpus.py
python scripts/build_story_index.py
python scripts/import_story_dialogues.py
python scripts/import_story_pages.py
python scripts/import_hutao_story_memory.py
~~~

这些脚本需要各自规定的原始 TextMap/公开档案已在本机。历史补缺向逐句导入器传 --extra-text-map，可重复。现版本文本优先，旧快照只补缺。改源数据后重新导出、在新目录建向量、重启服务与桌宠；不能只改 manifest 的版本号骗过校验。

## 9. 排错与扩展边界

| 现象/需求 | 入口 |
|---|---|
| 闲聊误触发/剧情漏触发 | QueryAnalyzer + 路由用例 |
| 人物对但事件错 | 内容词、实体、RRF、逐题 hits |
| 只记前半段 | 场景/分支元数据、上下文预算 |
| 态度前后说反 | 完整短篇 + 时序约束 + 人工事实评分 |
| 文本快但语音慢 | 独立检查 TTS 推理/队列 |
| Dense 配了未生效 | Trace.Warnings、/health、409/端口；评测有实际使用门禁 |
| 新角色 | Corpus/主题/Perspective + 角色隔离，不共享胡桃亲历身份 |
| 换模型/ANN/Reranker | IStorySemanticSearch，稳定 ID 不变，重建与消融 |
| HTTP/网页宿主 | 复用 IStoryRagService，新增适配器 |
| 流式首字 | 扩展 LLM 流式契约；当前未实现 |
| 自动刷新 | 后续做原子快照交换，不原地改字典 |

TextResult 和 AgentTurnResult 都保存 Story/StoryAnswer，方便未来开发者证据面板；当前普通 UI 不显示长篇来源，也不朗读引用 ID。

未实现：完整语义蕴含验证、全版本无缺口语料、长会话持久剧情指代、神经重排器、LLM 流式首字、自动模型服务管理。应依据失败用例迭代，而不是把接口预留算作完成。
