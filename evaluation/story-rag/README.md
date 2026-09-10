# 胡桃剧情 RAG 评测入口

当前默认集：`benchmark-v2.jsonl` + `benchmark-v3-complex.jsonl` + `benchmark-v4-agentic.jsonl`，259 个问题族、307 次执行。v2 是稳定回归集，v3 是复杂联动 challenge，v4.1 是 100 个混合意图场景；原 `golden.jsonl` 为历史种子，不再是默认集。

v4 包含剧情 30、混合任务 20、无关任务 15、玩梗 10、现实情绪 10、澄清 5、部分可答/未知 10。19 题参考米游社公开搜索返回的讨论内容，均附来源；其余原创。详见 [v4 数据集说明](benchmark-v4-guide.md) 和 [来源记录](benchmark-v4-sources.json)。

只跑 v4：

```powershell
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --cases evaluation/story-rag/benchmark-v4-agentic.jsonl --out evaluation/story-rag/results/v4-100-lexical
```

真实 Planner：在上述命令中增加 `--react-live`（已有安全配置的 API Key）。不加 `--react-limit` 就跑完 100 个问题族；不设置 `--cases` 则会跑默认全部版本。

要验证真实 ReAct Planner，在全量命令后增加 `--react-live --react-limit 10`；结果会写入同一个 `report.json` 的 `react` 字段。

先读[系统白盒文档](../../docs/story-rag.md)、[指标/人工评分规范](../../docs/story-rag-benchmark.md)与[v3 素材来源说明](benchmark-v3-sources.md)。

## 常用命令

在仓库根目录：

~~~powershell
# 完整轻量检索；默认不调用 DeepSeek、不生成语音
dotnet run --project agent/tools/HuTao.StoryRag.Eval --no-restore -p:UseSharedCompilation=false -- --out evaluation/story-rag/results/v3-lexical --strict

# 只跑复杂联动集（不改变默认回归集）
dotnet run --project agent/tools/HuTao.StoryRag.Eval --no-restore -p:UseSharedCompilation=false -- --cases evaluation/story-rag/benchmark-v3-complex.jsonl --out evaluation/story-rag/results/v3-complex-lexical

# 先按系统文档启动本机 BGE，再评混合模式
dotnet run --project agent/tools/HuTao.StoryRag.Eval --no-restore -p:UseSharedCompilation=false -- --semantic http://127.0.0.1:9891/ --out evaluation/story-rag/results/v3-hybrid --strict

# 相同语料/测试集上的模块消融，结构测试照常运行
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --ablation no-memory --out evaluation/story-rag/results/no-memory
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --ablation no-expansion --out evaluation/story-rag/results/no-expansion

# 看某个自然问题的全部计划/证据/分数
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --query "你的帽子是谁传给你的？"

# 已启动真实 Dense 服务时检查向量范数、版本、HTTP 边界等
voice/.venv/Scripts/python.exe scripts/test_story_semantic_service.py --index .tmp/story-dense-v1 --out evaluation/story-rag/results/semantic-smoke.json

# 显式 --live 或 --react-live 会调用付费 API；需要已有环境变量；--live-all-variants 控制旧答案评测的问法变体
dotnet run --project agent/tools/HuTao.StoryRag.Eval --no-restore -p:UseSharedCompilation=false -- --live --live-limit 10 --out evaluation/story-rag/results/live-small
dotnet run --project agent/tools/HuTao.StoryRag.Eval --no-restore -p:UseSharedCompilation=false -- --live --live-limit 10 --live-all-variants --out evaluation/story-rag/results/live-small-all
~~~

`--cases` 支持用分号分隔多个 JSONL 文件；每个复杂 case 可标注 difficulty、reasoning_types、source_scopes、relation_chain、independent_claims、expected_corrections、forbidden_inferences、conversation_id 和 turn_id。后四类字段用于白盒诊断/人工盲评，不会泄漏进检索器。

结果目录：

- report.md：概要、门禁、所有失败执行。
- report.json：数据集/语料哈希、冷/热/缓存性能、按 split/category/difficulty/reasoning/source 分层指标、复杂度曲线、混淆矩阵、结构测试、逐题锚点/窗口/分数/告警。
- live-review.json：仅 --live 生成，待人工审核，不自动提交 Git。

--cases 可指定同 schema 的 JSONL。--strict 在硬门禁失败时返回非零；不加时也不会吞掉结构测试失败。

## 如何看报告

先看语料告警和 configured_dense_actually_used，再看路由，之后看 Anchor 与 Context 指标；最后打开失败题的 hits 检查原因。Context 覆盖成功但 Anchor 排名差也是需要改善的现象。

regression 已用于开发，challenge 也是可见难例，均不是全新盲测。关键词覆盖、引用存在和结构验证不能代替人工事实/自然度评分。当前真实模型端到端质量请标“未测”，不要引用旧 10 条种子的满分当作当前系统能力。

[最近轻量报告](results/lexical/report.md) · [最近混合报告](results/hybrid/report.md)
