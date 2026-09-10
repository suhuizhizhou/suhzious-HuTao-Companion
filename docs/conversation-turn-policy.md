# 对话与主动搭话边界

## 2026-09-10：拒答之后又补答

聊天历史中，15:01:59 是固定 RAG 兜底，15:02:27 和 15:04:58 又出现钟离介绍。旧代码中检索全部 await 后才返回气泡，不会把子任务结果抢先播报；但心跳带着旧历史走无检索 LLM，可能再次回答同一问题。旧日志没有轮次 ID，所以不能据此百分百复原当时每段的触发来源。

## 本次规则

- 心跳初始化即进入冷却，用户发送、整轮气泡/音频结束、提醒、开场和文档朗读结束重新计算 120 秒冷却。
- 调度器空闲读取失败默认不打扰。心跳回到 WPF Dispatcher 后再次检查冷却、正在说话和角色切换状态；后台检查过期不会强行插话。
- ReactAgent 的文字生成用 SemaphoreSlim 串行化，避免两个生成调用同时读写历史。音频队列由 WPF 既有门负责。
- 主动心跳暂时只输出按角色选择的本地邀请，不调用 LLM、不携带旧问答进行补答，也不修改聊天的模型历史。用户明确提问才走现有 Story RAG / Composer 校验链。提醒等独立功能保留。
- 这是明确的能力收窄：自由生成的主动闲聊/剧情续答暂时停用。未来恢复必须有明确的新话题意图、统一事实约束和单轮提交验证，不能仅靠提示词要求模型不要重复。
- 引用/JSON 校验失败或模型不可用，不再说成“没有可靠资料”；自然兜底与真正资料不足分开。这里不伪造正确答案，也不会无证据输出“亲自请来钟离”等断言。

## 日志

`%LOCALAPPDATA%/HuTaoCompanion/logs/runtime.log` 记录生成轮次 `turn_id`、`trigger=user/proactive`、开始/完成/失败、RAG 状态、证据数、段数、Composer `path` 与 `issues`。

例如 `validation-fallback + missing_citation` 表示引用校验失败，不等于库里没有钟离；`model-failure-fallback + model_unavailable` 表示模型调用失败。每轮只能选择一个结果出口。日志不写用户问题、模型正文或 HTTP 响应体；目前生成轮次日志不是逐个气泡播放时刻日志。

## 验证（不运行模型/播放器/GPU）

```powershell
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --conversation-checks
dotnet run --project agent/tools/HuTao.StoryRag.Eval -- --speech-checks
```

使用虚拟时钟验证冷却，用受控离线 LLM 验证并发串行化和主动不重答，用临时日志验证诊断字段。没有启动桌宠实机推理，以免影响后台训练；运行中的旧桌宠需下次手动重启才载入新构建。
