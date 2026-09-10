# ReAct → Story RAG 演进设计

## 目标

将当前“一次完整问题、一次剧情检索”升级为受控的多轮 ReAct 检索：先拆分用户问题，再逐个观察证据，最后交给现有 Verifier/Synthesizer 生成回答。该阶段不改变 TTS、角色人设和历史存储接口。

## 当前实现

`ReactRetrievalLoop` 位于 `agent/src/HuTao.Agent.Core/Core/ReactRetrievalLoop.cs`。

执行流程：

```text
用户问题
  ↓
按中文标点拆分子问题
  ↓
每个子问题调用 StoryKnowledgeTool
  ↓
记录 status / evidence / coverage
  ↓
按证据 ID 去重并合并成 Evidence Pool
  ↓
把每轮观察轨迹注入 Agent observation
  ↓
复用原有 StoryAnswerComposer、Verifier 和语音链路
```

每轮都有最大次数限制，避免模型或检索器无限循环。`ReactRetrievalOptions` 中的 `MaxRounds`、`MaxSubQueries` 和 `MinimumCoverage` 是后续调参入口。

## 设计边界

当前版本是确定性的 ReAct 检索循环，不让 LLM 自由生成工具调用，因此可回归、可审计。每个子问题的结果会按证据 ID 去重，合并为 `EvidencePool` 后再进入 `StoryAnswerComposer`，不再只把最佳子问题作为答案依据。子问题拆分暂时使用标点和去重规则，后续可替换为结构化 Planner，而不改变 `ReactRetrievalLoop` 的输出模型。

## 已实现的 Agentic RAG 能力

当前 `ReactRetrievalLoop` 已增加结构化 Planner、依赖执行和补检索：

1. Planner 优先要求 LLM 返回 `id/query/required_facts/depends_on` JSON；解析失败自动回退到确定性拆分。
2. 无依赖子任务通过 `Task.WhenAll` 并行检索，有依赖任务按轮次执行。
3. Evidence Pool 按证据 ID 去重并合并所有子任务结果。
4. 对缺失事实组发起定向补检索，并把仍缺失的事实写入证据边界警告。
5. Claim 校验检查引用池、因果连接词和时间顺序锚点，向回答协议传递限制。

当前停止条件为：依赖任务完成且完成补检索，或达到最大检索轮数；证据不足时状态降为 `Tentative`，由现有 Composer 生成部分回答或安全拒答。后续可把确定性 Claim 校验替换为逐条声明级验证器。

## 测试与观测

应记录每轮 Query、命中数量、覆盖率、状态、耗时和停止原因。指标包括子问题拆解准确率、证据组覆盖率、多跳链完整率、因果方向准确率、未知边界遵守率、引用覆盖率和总检索轮数。

## 一体化评测命令

2026-09-10：v4 已重整为 100 个场景（剧情、混合、无关、玩梗、现实情绪、澄清、未知），默认合集 259 族/307 次离线执行。[v4.1 数据集与评分边界](../evaluation/story-rag/benchmark-v4-guide.md) 说明真实来源、依赖标注、分范围指标，以及哪些能力尚不能自动评分。旧 4 题的报告不代表新集表现。

默认评测器现在自动合并 v2、v3 和 v4 数据集，不再需要分别维护多套入口。离线检索与结构验证：

```powershell
dotnet agent/tools/HuTao.StoryRag.Eval/bin/Debug/net9.0/HuTao.StoryRag.Eval.dll --out evaluation/story-rag/results/full
```

真实 DeepSeek Planner 验证（需要 `DEEPSEEK_API_KEY`）：

```powershell
dotnet agent/tools/HuTao.StoryRag.Eval/bin/Debug/net9.0/HuTao.StoryRag.Eval.dll --react-live --react-limit 10 --out evaluation/story-rag/results/full-react
```

同一份 `report.json` 同时保存离线检索指标和 `react` 指标，包括 Planner 子任务数、并行批次、补检索次数、证据组覆盖率及每题的步骤轨迹。`--react-limit` 用于控制 API 成本；去掉它才运行整个数据集。
