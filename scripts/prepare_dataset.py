#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把胡桃语音包 + 文字标签（JSONL manifest）整理成 GPT-SoVITS few-shot 参考音频清单。

manifest 每行一条（见 CORE.md §4.1）：
  {"id": "hutao_0001", "audio": "raw/hutao_0001.wav", "text": "大丘丘病了……",
   "speaker": "hutao", "emotion": "轻松", "scene": "日常对话",
   "source": "游戏内语音", "duration_ms": 3200, "sample_rate": 44100}

用法示例：
  python scripts/prepare_dataset.py \
      --manifest data/voice/labels/manifest.jsonl \
      --audio-root data/voice \
      --out data/voice/dataset/refs.json \
      --min-sec 3 --max-sec 10
"""

import argparse
import json
import subprocess
import sys
from pathlib import Path


def probe_duration(path: Path) -> float:
    """用 ffprobe 读取真实时长（秒），失败返回 -1。"""
    try:
        out = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
            capture_output=True, text=True, timeout=30,
        )
        return float(out.stdout.strip())
    except Exception:
        return -1.0


def load_manifest(path: Path) -> list[dict]:
    items = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        items.append(json.loads(line))
    return items


def main() -> int:
    p = argparse.ArgumentParser(description="生成 GPT-SoVITS few-shot 参考音频清单")
    p.add_argument("--manifest", required=True, help="JSONL 标签文件路径")
    p.add_argument("--audio-root", required=True, help="音频根目录（manifest 中 audio 相对此路径）")
    p.add_argument("--out", required=True, help="输出参考清单 JSON 路径")
    p.add_argument("--min-sec", type=float, default=3.0, help="最短时长（秒）")
    p.add_argument("--max-sec", type=float, default=10.0, help="最长时长（秒）")
    p.add_argument("--max-refs", type=int, default=20, help="最多挑选条数")
    p.add_argument("--prefer-emotions", default="", help="优先情绪，逗号分隔（如 活泼,俏皮）")
    args = p.parse_args()

    root = Path(args.audio_root).resolve()
    manifest = Path(args.manifest).resolve()
    if not manifest.exists():
        print(f"[error] manifest 不存在: {manifest}", file=sys.stderr)
        return 1

    prefer = [e.strip() for e in args.prefer_emotions.split(",") if e.strip()]

    refs = []
    for item in load_manifest(manifest):
        audio = (root / item["audio"]).resolve()
        if not audio.exists():
            print(f"[skip] 音频缺失: {item['audio']}", file=sys.stderr)
            continue

        # 时长优先用 manifest 的 duration_ms，否则 ffprobe 实测
        dur = item.get("duration_ms")
        if dur is not None:
            dur = dur / 1000.0
        else:
            dur = probe_duration(audio)

        if dur < 0:
            print(f"[skip] 无法获取时长: {item['audio']}", file=sys.stderr)
            continue
        if not (args.min_sec <= dur <= args.max_sec):
            continue

        refs.append({
            "id": item.get("id", audio.stem),
            "audio": str(audio),
            "text": item.get("text", ""),
            "prompt_lang": item.get("lang", "zh"),
            "emotion": item.get("emotion", ""),
            "scene": item.get("scene", ""),
            "duration_sec": round(dur, 2),
        })

    # 情绪优先排序：命中 prefer 的排前面，其余按原顺序
    if prefer:
        refs.sort(key=lambda r: 0 if r["emotion"] in prefer else 1)

    refs = refs[: args.max_refs]

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(refs, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"[done] 挑选 {len(refs)} 条参考音频 -> {out}")
    for r in refs[:5]:
        print(f"  - {r['id']}  {r['duration_sec']}s  [{r['emotion']}]  {r['text'][:20]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
