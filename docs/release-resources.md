# Release 资源清单

## 完整语音版已包含

- `HuTao.Pet.exe` 及 .NET 9 Windows x64 自包含运行库；
- 胡桃、芙宁娜、可莉的人设、情绪参考配置和聊天原声库；
- GPT-SoVITS v3 推理代码、预训练模型、BERT/CNHubert/BigVGAN 等运行权重；
- 经 `conda-pack` 处理的便携 Python 3.12/PyTorch CUDA 环境；
- 项目额外的 GPT-SoVITS Python 依赖；
- FFmpeg 与文档朗读所需组件；
- 剧情摘要向量索引和逐句剧情资料；
- `.env.example`、启动脚本和资源检查脚本。

完整包解压后无需另外安装 .NET、Python、GPT-SoVITS 或下载模型。首次启动时程序会执行一次 `conda-unpack`，将便携 Python 适配到当前解压路径；请避免启动后再移动目录，如果需要移动，建议重新解压一份。

## 唯一需要单独提供的配置

DeepSeek API Key 不会写入或打包进 Release。将 `.env.example` 复制为 `.env`，填写：

```ini
DEEPSEEK_API_KEY=sk-你的key
```

未填写时应用仍能启动，但使用离线 Mock 对话。API Key 只保存在使用者本地目录中。

## 硬件与系统要求

- Windows 10/11 x64；
- 推荐 NVIDIA GPU，显存不少于 8 GB；
- 与打包的 PyTorch CUDA 版本兼容的 NVIDIA 驱动；
- 约 12 GB 可用磁盘空间，解压分卷时还需要额外临时空间。

如果没有可用 CUDA，用户可以在 `.env` 中设置 `HU_TAO_TTS_DEVICE=cpu` 和 `HU_TAO_TTS_HALF=false`，但合成速度会显著下降。

## 分发与合规

完整包包含从游戏中提取的角色语音与剧情资料，不适合上传到公开 GitHub Release。建议只在本人设备或明确获得授权的私有环境使用。项目、GPT-SoVITS 以及模型分别受各自许可证约束；角色形象、声线和台词版权归其权利人所有。
