# HuTao Companion v0.1.0

## 启动

双击 `run.bat`。这是 Windows 10/11 x64 自包含版本，无需另外安装 .NET。

本包已经包含 GPT-SoVITS v3、便携 Python/PyTorch 环境、三角色参考音频与原声库、FFmpeg 和剧情 RAG 数据。首次启动会自动适配便携 Python 的解压路径，模型加载可能需要一段时间；后续会复用常驻模型。

唯一需要另外配置的是 DeepSeek API：将 `.env.example` 复制为 `.env`，填写 `DEEPSEEK_API_KEY` 后重新启动。未填写时可以启动界面，但对话使用离线 Mock。

## 窗口操作

- `◫`：缩成角色头像与最近一条气泡；
- `—`：完全隐藏到系统托盘；
- 双击头像或托盘图标：恢复完整聊天窗口；
- 右键托盘图标：切换三种显示模式、切换始终置顶或退出。

窗口偏好保存在 `%LOCALAPPDATA%\HuTaoCompanion\ui-settings.json`，不会写回发布目录。

运行资源和显卡要求见 `RESOURCE_MANIFEST.md`。本包含有游戏提取资源，仅限个人学习与技术演示，请勿公开传播或商用。
