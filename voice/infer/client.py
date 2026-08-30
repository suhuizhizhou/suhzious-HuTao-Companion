#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GPT-SoVITS v3 推理客户端（调用官方 api_v2.py 服务）。

先启动服务：
  cd GPT-SoVITS && .\\.venv\\Scripts\\python api_v2.py -a 127.0.0.1 -p 9880

用法示例（单句）：
  python voice/infer/client.py \
      --text "大丘丘病了，二丘丘瞧，三丘丘采药，四丘丘熬" \
      --ref-audio data/voice/raw/hutao_0001.wav \
      --prompt-text "（该参考音频对应的文字）" \
      --out data/voice/dataset/out/hutao_out.wav

说明：api_v2.py 的端点和字段在不同版本可能略有差异，
     以 http://127.0.0.1:9880/docs 为准；本脚本已对常见响应做兼容。
"""

import argparse
import base64
import json
import sys
from pathlib import Path

try:
    import requests
except ImportError:
    print("[error] 缺少 requests，请先: pip install requests", file=sys.stderr)
    sys.exit(1)


def build_payload(args) -> dict:
    """构造请求体。字段名以 api_v2.py 的 /docs 为准，可在此调整。"""
    return {
        "text": args.text,
        "text_lang": args.text_lang,
        "ref_audio_path": args.ref_audio,
        "prompt_text": args.prompt_text,
        "prompt_lang": args.prompt_lang,
        "top_k": args.top_k,
        "top_p": args.top_p,
        "temperature": args.temperature,
        "text_split_method": args.text_split_method,
        "batch_size": args.batch_size,
        "speed_factor": args.speed_factor,
        "fragment_interval": args.fragment_interval,
        "seed": args.seed,
        "media_type": args.media_type,
        "streaming_mode": args.streaming_mode,
    }


def save_audio(data_uri_or_bytes: str | bytes, out: Path) -> None:
    """响应里的 audio 可能是 data URI 或二进制，统一落盘。"""
    if isinstance(data_uri_or_bytes, bytes):
        out.write_bytes(data_uri_or_bytes)
        return
    s = str(data_uri_or_bytes)
    if s.startswith("data:"):
        # data:audio/wav;base64,....
        b64 = s.split(",", 1)[1]
        out.write_bytes(base64.b64decode(b64))
    else:
        # 尝试按 base64 字符串解析
        out.write_bytes(base64.b64decode(s))


def main() -> int:
    p = argparse.ArgumentParser(description="GPT-SoVITS v3 few-shot 推理客户端")
    p.add_argument("--api", default="http://127.0.0.1:9880", help="api_v2.py 服务地址")
    p.add_argument("--text", required=True, help="要合成的目标文本")
    p.add_argument("--text-lang", default="zh")
    p.add_argument("--ref-audio", required=True, help="参考音频路径（胡桃原声）")
    p.add_argument("--prompt-text", required=True, help="参考音频对应文字")
    p.add_argument("--prompt-lang", default="zh")
    p.add_argument("--top-k", type=int, default=5)
    p.add_argument("--top-p", type=float, default=1.0)
    p.add_argument("--temperature", type=float, default=1.0)
    p.add_argument("--text-split-method", default="cut0")
    p.add_argument("--batch-size", type=int, default=1)
    p.add_argument("--speed-factor", type=float, default=1.0)
    p.add_argument("--fragment-interval", type=float, default=0.3)
    p.add_argument("--seed", type=int, default=-1)
    p.add_argument("--media-type", default="wav")
    p.add_argument("--streaming-mode", action="store_true", help="v3 流式输出")
    p.add_argument("--out", required=True, help="输出音频路径")
    args = p.parse_args()

    if not Path(args.ref_audio).exists():
        print(f"[error] 参考音频不存在: {args.ref_audio}", file=sys.stderr)
        return 1

    url = args.api.rstrip("/") + "/tts"
    payload = build_payload(args)

    print(f"[request] POST {url}")
    print(f"[request] text={args.text[:30]}... ref={args.ref_audio}")

    try:
        resp = requests.post(url, json=payload, timeout=300)
    except requests.RequestException as e:
        print(f"[error] 请求失败: {e}", file=sys.stderr)
        print("[hint] 确认服务已启动，且端点/字段与 /docs 一致（也可能是 /tts 改为 /）",
              file=sys.stderr)
        return 1

    if resp.status_code != 200:
        print(f"[error] HTTP {resp.status_code}: {resp.text[:500]}", file=sys.stderr)
        return 1

    ctype = resp.headers.get("content-type", "")
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)

    if "json" in ctype:
        data = resp.json()
        # 兼容常见包装：{"code":0,"data":{"audio": ...}} 或直接 {"audio": ...}
        if isinstance(data, dict) and "data" in data:
            data = data["data"]
        audio = data.get("audio") if isinstance(data, dict) else None
        if not audio:
            print(f"[error] 响应中未找到 audio 字段: {json.dumps(data, ensure_ascii=False)[:500]}",
                  file=sys.stderr)
            return 1
        save_audio(audio, out)
    else:
        # 直接返回二进制音频
        out.write_bytes(resp.content)

    print(f"[done] 已保存 {out} ({out.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
