# -*- coding: utf-8 -*-
"""GPT-SoVITS v3 few-shot 推理（胡桃声线基线）。

用法（在 voice/ 目录下）:
  .venv/Scripts/python infer/few_shot_infer.py \
      --ref_audio ../data/voice/hutao/wav/<hash>.wav \
      --ref_text "哼哼，切勿质疑我的业务能力！" \
      --text "大丘丘病了，二丘丘瞧，三丘丘采药，四丘丘熬。" \
      --out ../data/voice/hutao/output.wav
"""
import argparse
import os
import sys

GS_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "GPT-SoVITS-main"))
sys.path.insert(0, GS_ROOT)
sys.path.insert(0, os.path.join(GS_ROOT, "GPT_SoVITS"))  # 供 `from text.LangSegmenter import` 使用
os.chdir(GS_ROOT)  # GPT-SoVITS 依赖相对路径（configs 等）

from GPT_SoVITS.inference_webui import change_gpt_weights, change_sovits_weights, get_tts_wav  # noqa: E402
from tools.i18n.i18n import I18nAuto  # noqa: E402
import soundfile as sf  # noqa: E402

i18n = I18nAuto()


def main():
    ap = argparse.ArgumentParser(description="GPT-SoVITS v3 few-shot 推理")
    ap.add_argument("--ref_audio", required=True, help="参考音频路径（胡桃 3-10s）")
    ap.add_argument("--ref_text", required=True, help="参考音频对应文本")
    ap.add_argument("--text", required=True, help="要合成的目标文本")
    ap.add_argument("--out", required=True, help="输出 wav 路径")
    ap.add_argument("--gpt_model", default="GPT_SoVITS/pretrained_models/s1v3.ckpt")
    ap.add_argument("--sovits_model", default="GPT_SoVITS/pretrained_models/s2Gv3.pth")
    ap.add_argument("--top_k", type=int, default=20)
    ap.add_argument("--top_p", type=float, default=0.6)
    ap.add_argument("--temperature", type=float, default=0.6)
    args = ap.parse_args()

    print(f"[load] gpt={args.gpt_model} sovits={args.sovits_model}")
    change_gpt_weights(gpt_path=args.gpt_model)
    # change_sovits_weights 是 generator：模型加载在首个 yield 之前完成；
    # 后续 UI yield 会因缺 prompt_language 抛异常，官方用 next()+try/except 忽略。
    try:
        next(change_sovits_weights(sovits_path=args.sovits_model))
    except Exception as e:
        print(f"[warn] sovits ui yield ignored: {e}")

    print(f"[synth] ref={args.ref_audio}")
    print(f"[synth] ref_text={args.ref_text}")
    print(f"[synth] text={args.text}")

    result = get_tts_wav(
        ref_wav_path=args.ref_audio,
        prompt_text=args.ref_text,
        prompt_language=i18n("中文"),
        text=args.text,
        text_language=i18n("中文"),
        top_k=args.top_k,
        top_p=args.top_p,
        temperature=args.temperature,
    )
    result_list = list(result)
    if not result_list:
        print("[error] 推理无输出", file=sys.stderr)
        return 1
    sr, audio = result_list[-1]
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    sf.write(args.out, audio, sr)
    print(f"[done] {args.out} (sr={sr}, len={len(audio)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
