# 可莉语音资源

可莉的人设和 UI 可以在没有语音资源时运行，TTS 会自动降级为文字气泡。本次本地提取结果为 592 条中文 WAV、约 45.2 分钟；`emotion-references.json` 已配置 6 种情绪共 24 条参考音频，默认参考为 `61335463fedb347a.wav`（“这是可莉创办的小魔女会哦。我是小魔女可莉。”）。所有参考音频均控制在 GPT-SoVITS 要求的 3–10 秒范围内。

如需从原神包体重新生成资源，先准备 `genshin-voice` 的 Klee JSON 标签和 `tools/vgmstream-cli.exe`，然后执行：

```powershell
node scripts/extract_hash_map.mjs tools/Klee_extracted tools/Klee_hash_map.json
node scripts/extract_character_manual.mjs `
  "D:\\ggy\\原神\\Genshin Impact\\Genshin Impact Game\\YuanShen_Data\\StreamingAssets\\AudioAssets\\Chinese" `
  tools/Klee_hash_map.json data/voice/klee tools/vgmstream-cli.exe
node scripts/normalize_manual_manifest.mjs `
  data/voice/klee/manifest_manual.jsonl data/voice/klee
node scripts/pick_quotes.mjs `
  data/voice/klee/manifest.jsonl data/persona/klee/quotes.json `
  "可莉|嘟嘟可|炸弹|琴团长|小魔女|冒险|嘿嘿|哒哒哒|好耶|不要告诉"
```

如果使用自己的授权音频，只需保持 `wav/<hash>.wav` 文件名并同步修改 `emotion-references.json`；桌宠会复用常驻 GPT-SoVITS 服务，不会每句重新加载模型。

项目不提交游戏原始音频、模型权重或大体积数据；请仅在本地准备资源，并遵守游戏内容的使用许可。
