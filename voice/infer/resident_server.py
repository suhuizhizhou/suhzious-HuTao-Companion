#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""常驻 GPT-SoVITS v3 推理服务。

启动一次后，GPT、SoVITS、BERT、CNHuBERT 和 vocoder 会一直保留在进程/显存中。
桌宠通过 HTTP 调用 /tts，避免每句话都重新创建 Python 进程和加载模型。

这个脚本只依赖 GPT-SoVITS 本体提供的 TTS 类，不修改第三方仓库中的 api_v2.py。
"""

from __future__ import annotations

import argparse
import asyncio
import os
import sys
import threading
from pathlib import Path
from typing import Any


SCRIPT_DIR = Path(__file__).resolve().parent
GS_ROOT = SCRIPT_DIR.parent / "GPT-SoVITS-main"
if not GS_ROOT.is_dir():
    raise FileNotFoundError(f"GPT-SoVITS 目录不存在: {GS_ROOT}")

os.chdir(GS_ROOT)
sys.path.insert(0, str(GS_ROOT))
sys.path.insert(0, str(GS_ROOT / "GPT_SoVITS"))

import numpy as np  # noqa: E402
import soundfile as sf  # noqa: E402
import uvicorn  # noqa: E402
from fastapi import FastAPI  # noqa: E402
from fastapi.responses import JSONResponse, Response  # noqa: E402
from pydantic import BaseModel  # noqa: E402

from GPT_SoVITS.TTS_infer_pack.TTS import TTS, TTS_Config  # noqa: E402


class TtsRequest(BaseModel):
    text: str
    text_lang: str = "zh"
    ref_audio_path: str
    prompt_text: str = ""
    prompt_lang: str = "zh"
    top_k: int = 20
    top_p: float = 0.6
    temperature: float = 0.6
    text_split_method: str = "cut5"
    batch_size: int = 1
    speed_factor: float = 1.0
    fragment_interval: float = 0.3
    seed: int = -1
    media_type: str = "wav"
    # GPT-SoVITS v3/v4 会自动回退到分段返回模式；这里默认关闭，返回完整 WAV。
    streaming_mode: bool = False
    parallel_infer: bool = True
    sample_steps: int = 32
    super_sampling: bool = False


class WarmupRequest(BaseModel):
    ref_audio_path: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Resident GPT-SoVITS v3 HTTP server")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9881)
    parser.add_argument("--gpt-model", required=True)
    parser.add_argument("--sovits-model", required=True)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--half", action="store_true")
    return parser.parse_args()


ARGS = parse_args()
DEVICE = ARGS.device
USE_HALF = bool(ARGS.half and DEVICE != "cpu")

# TTS_Config 需要 custom 层，否则会回退到仓库默认的 v2 配置。
config = TTS_Config(
    {
        "custom": {
            "bert_base_path": str(GS_ROOT / "GPT_SoVITS" / "pretrained_models" / "chinese-roberta-wwm-ext-large"),
            "cnhuhbert_base_path": str(GS_ROOT / "GPT_SoVITS" / "pretrained_models" / "chinese-hubert-base"),
            "device": DEVICE,
            "is_half": USE_HALF,
            "t2s_weights_path": str(Path(ARGS.gpt_model).resolve()),
            "vits_weights_path": str(Path(ARGS.sovits_model).resolve()),
            "version": "v3",
        }
    }
)

print(f"[resident] loading models on {config.device} (half={config.is_half})", flush=True)
tts = TTS(config)
inference_lock = threading.Lock()

app = FastAPI(title="HuTao Companion Resident TTS")


def _validate_request(req: TtsRequest) -> str | None:
    if not req.text.strip():
        return "text is required"
    if not req.ref_audio_path:
        return "ref_audio_path is required"
    if not Path(req.ref_audio_path).is_file():
        return f"reference audio does not exist: {req.ref_audio_path}"
    if req.media_type != "wav":
        return "resident server currently supports media_type=wav only"
    return None


def _synthesize(req: TtsRequest) -> tuple[int, np.ndarray]:
    # TTS.run 是同步生成器；锁保证同一组 GPU 权重不会被并发请求交叉修改。
    payload: dict[str, Any] = req.model_dump()
    with inference_lock:
        result = next(tts.run(payload), None)
    if result is None:
        raise RuntimeError("GPT-SoVITS returned no audio")
    return result


def _warmup(ref_audio_path: str) -> None:
    if not Path(ref_audio_path).is_file():
        raise FileNotFoundError(f"reference audio does not exist: {ref_audio_path}")
    with inference_lock:
        # set_ref_audio 只计算并缓存参考音频特征，不生成一段无用语音。
        tts.set_ref_audio(ref_audio_path)


@app.get("/health")
async def health() -> dict[str, Any]:
    return {
        "status": "ready",
        "model": "gpt-sovits-v3",
        "device": str(config.device),
        "half": bool(config.is_half),
        "pid": os.getpid(),
    }


@app.post("/warmup")
async def warmup(req: WarmupRequest):
    try:
        await asyncio.to_thread(_warmup, req.ref_audio_path)
        return {"status": "ready", "reference": req.ref_audio_path}
    except Exception as exc:  # pragma: no cover - depends on local model/audio files
        return JSONResponse(status_code=400, content={"message": "warmup failed", "error": str(exc)})


@app.post("/tts")
async def tts_endpoint(req: TtsRequest):
    error = _validate_request(req)
    if error:
        return JSONResponse(status_code=400, content={"message": error})

    try:
        sample_rate, audio = await asyncio.to_thread(_synthesize, req)
        buffer = bytearray()
        # soundfile 直接写入内存，C# 端收到后再落盘为临时 WAV。
        import io

        output = io.BytesIO()
        sf.write(output, audio, sample_rate, format="WAV")
        buffer.extend(output.getvalue())
        return Response(bytes(buffer), media_type="audio/wav")
    except Exception as exc:  # pragma: no cover - depends on GPU/model runtime
        return JSONResponse(status_code=400, content={"message": "tts failed", "error": str(exc)})


if __name__ == "__main__":
    print(f"[resident] listening on http://{ARGS.host}:{ARGS.port}", flush=True)
    uvicorn.run(app, host=ARGS.host, port=ARGS.port, workers=1, log_level="warning")
