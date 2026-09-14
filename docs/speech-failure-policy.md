# 语音失败处理策略

## 用户可见行为

TTS 是聊天文本的可选附件，不是回复成功的前置条件。模型已经生成文字后，任意一段语音失败都执行：

1. 保留原有角色文本气泡；
2. 当前轮不再重复调用 TTS，避免连续失败和显存/进程抖动；
3. 后续新一轮对话可以重新尝试；
4. 动作气泡（`（……）`）永远不调用 TTS。

因此用户只会看到角色自然的文字，不会看到“退出码 1”“CUDA out of memory”“TTS 合成失败”等 OOC 技术信息。

旧版已存入历史的“（说话卡住了：…”和“切换角色出错了：…”程序消息会在加载时隐藏，不再进入角色上下文。原始记录不删除，用户自己发送的相同文本不隐藏。

## 诊断记录

`SpeechDeliverySession` 在单轮内管理文本—音频交付；底层 `GptSovitsTtsEngine` 同时排空 Python stdout/stderr，失败时抛出包含退出码和诊断尾部的 `TtsProcessException`。诊断写入：

```text
%LOCALAPPDATA%/HuTaoCompanion/logs/runtime.log
```

日志按 JSON Lines 写入，达到 2 MB 后滚动为 `runtime.log.previous`。API Key、Bearer Token 和 `api_key=` 等字段会脱敏，普通聊天文本不写入日志；TTS stderr 仅保留最后 16 KiB，并移除当前参考文本和目标文本。

## 异常边界

- 用户取消（`CancellationToken`）继续向上传播，不伪装成 TTS 故障。
- 缺少音频文件、空响应、Python 非零退出、HTTP 错误都会降级为文字并记日志。
- 日志目录无权限、磁盘写失败不能再次影响聊天。
- 文档朗读失败只显示角色化的“还没念完”提示；详细异常仅进入日志。
- 角色加载、聊天记录保存、MP3 打开失败同样不把异常字符串写成角色台词。

## 离线回归

不启动 Python、GPU、播放器即可运行：

```powershell
dotnet tests/HuTao.StoryRag.Eval/bin/Debug/net9.0/HuTao.StoryRag.Eval.dll --speech-checks
```

覆盖退出码、stderr 脱敏、文字保留、单轮不重试、下一轮恢复、原声直通、动作静音、取消传播和日志失败等路径。
