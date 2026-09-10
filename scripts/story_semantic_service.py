"""Optional local Chinese Dense retrieval. No LangChain/server framework dependency.
build: exported immutable source IDs -> real BGE embeddings -> versioned NumPy index
serve: loopback HTTP -> query embedding -> cosine top-k (source text stays in C#)
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

QUERY_PREFIX = "为这个句子生成表示以用于检索相关文章："

def dump(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")

class Encoder:
    def __init__(self, model, revision=None, device="cpu", local_only=False):
        import torch
        from transformers import AutoModel, AutoTokenizer
        self.torch = torch
        torch.set_num_threads(2)
        self.tokenizer = AutoTokenizer.from_pretrained(
            model, revision=revision, local_files_only=local_only, trust_remote_code=False)
        self.model = AutoModel.from_pretrained(
            model, revision=revision, local_files_only=local_only,
            trust_remote_code=False, use_safetensors=True).to(device).eval()
        self.device = device
        self.revision = getattr(self.model.config, "_commit_hash", None)
        self.lock = threading.Lock()

    def encode(self, texts, query=False):
        with self.lock, self.torch.inference_mode():
            if query:
                texts = [QUERY_PREFIX + text for text in texts]
            tokens = self.tokenizer(texts, padding=True, truncation=True, max_length=512, return_tensors="pt").to(self.device)
            hidden = self.model(**tokens).last_hidden_state[:, 0]  # BGE v1.5: CLS pooling
            vectors = self.torch.nn.functional.normalize(hidden, p=2, dim=1)
            return vectors.float().cpu().numpy()

def build(args):
    import numpy as np
    source = Path(args.input)
    with source.open(encoding="utf-8-sig") as f:
        version = json.loads(next(f))["corpus_version"]
        rows = [json.loads(line) for line in f if line.strip()]
    if not rows or len({r["id"] for r in rows}) != len(rows):
        raise ValueError("Empty corpus or duplicate IDs")
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError("Output must be empty: build a new directory, then switch endpoint; never overwrite an active index.")
    encoder = Encoder(args.model, args.revision, args.device, args.local_only)
    started = time.perf_counter()
    first = encoder.encode([r["text"] for r in rows[:args.batch_size]])
    vectors = np.lib.format.open_memmap(output / "vectors.npy", mode="w+", dtype=np.float32,
                                       shape=(len(rows), first.shape[1]))
    vectors[:len(first)] = first
    for offset in range(len(first), len(rows), args.batch_size):
        end = min(offset + args.batch_size, len(rows))
        vectors[offset:end] = encoder.encode([r["text"] for r in rows[offset:end]])
        if offset % (args.batch_size * 100) == 0:
            print(f"Embedded {end}/{len(rows)} ({time.perf_counter()-started:.0f}s)", flush=True)
    vectors.flush()
    dump(output / "ids.json", [r["id"] for r in rows])
    # Manifest written last. Incomplete builds cannot be served.
    dump(output / "manifest.json", {
        "schema": 1, "corpus_version": version, "model": args.model,
        "revision": encoder.revision or args.revision, "dimension": first.shape[1], "count": len(rows),
        "pooling": "cls-l2", "query_prefix": QUERY_PREFIX, "max_tokens": 512,
        "export_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "vectors_sha256": hashlib.sha256((output / "vectors.npy").read_bytes()).hexdigest(),
        "build_seconds": time.perf_counter()-started,
    })
    print(f"Built {len(rows)} vectors at {output}", flush=True)

class Index:
    def __init__(self, folder, device):
        import numpy as np
        self.np = np
        root = Path(folder)
        self.manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
        if self.manifest["schema"] != 1 or self.manifest["pooling"] != "cls-l2":
            raise ValueError("Unsupported semantic index")
        if hashlib.sha256((root / "vectors.npy").read_bytes()).hexdigest() != self.manifest["vectors_sha256"]:
            raise ValueError("Vector checksum mismatch")
        self.ids = json.loads((root / "ids.json").read_text(encoding="utf-8"))
        self.vectors = np.load(root / "vectors.npy", mmap_mode="r", allow_pickle=False)
        if self.vectors.shape != (len(self.ids), self.manifest["dimension"]):
            raise ValueError("Index shape mismatch")
        self.encoder = Encoder(self.manifest["model"], self.manifest["revision"], device, local_only=True)
        self.encoder.encode(["热身"], query=True)

    def search(self, query, k):
        vector = self.encoder.encode([query], query=True)[0]
        scores = self.vectors @ vector
        k = min(k, len(scores))
        selected = self.np.argpartition(-scores, k-1)[:k]
        selected = sorted(selected, key=lambda i: (-float(scores[i]), self.ids[i]))
        return [{"id": self.ids[i], "score": float(scores[i])} for i in selected]

def serve(args):
    index = Index(args.index, args.device)
    slots = threading.BoundedSemaphore(2)
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass  # Query content never enters access logs.
        def send_json(self, code, value):
            data = json.dumps(value, ensure_ascii=False).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass  # C# may have exceeded its bounded retrieval deadline.
        def do_GET(self):
            if self.path != "/health":
                return self.send_json(404, {"error": "not_found"})
            self.send_json(200, {"ready": True, "corpus_version": index.manifest["corpus_version"],
                                  "count": len(index.ids), "model": index.manifest["model"]})
        def do_POST(self):
            # Reject browser origins, unexpected hosts, excessive bodies and queued model work.
            if self.headers.get("Origin") or self.headers.get("Host") not in (f"127.0.0.1:{args.port}", f"localhost:{args.port}"):
                return self.send_json(403, {"error": "local_clients_only"})
            if self.path != "/search":
                return self.send_json(404, {"error": "not_found"})
            try:
                size = int(self.headers.get("Content-Length", "0"))
                if not 0 < size <= 20000:
                    return self.send_json(413, {"error": "body_size"})
                request = json.loads(self.rfile.read(size))
                query, k = request["query"], request.get("top_k", 40)
                if not isinstance(query, str) or not 1 <= len(query) <= 4000 or type(k) is not int or not 1 <= k <= 100:
                    return self.send_json(400, {"error": "invalid_query"})
                if request.get("corpus_version") != index.manifest["corpus_version"]:
                    return self.send_json(409, {"error": "stale_corpus_rebuild_index"})
                if not slots.acquire(blocking=False):
                    return self.send_json(429, {"error": "busy"})
                try:
                    result = index.search(query, k)
                finally:
                    slots.release()
                self.send_json(200, result)
            except (ValueError, KeyError, TypeError):
                self.send_json(400, {"error": "invalid_json_contract"})
            except Exception:
                self.send_json(503, {"error": "search_failed"})
        def setup(self):
            super().setup()
            self.connection.settimeout(5)
    print(f"Ready at http://127.0.0.1:{args.port}/ ({len(index.ids)} lines)", flush=True)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("build")
    b.add_argument("--input", required=True)
    b.add_argument("--output", required=True)
    b.add_argument("--model", default="BAAI/bge-small-zh-v1.5")
    b.add_argument("--revision", default=None)
    b.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    b.add_argument("--batch-size", type=int, default=32)
    b.add_argument("--local-only", action="store_true")
    b.add_argument("--hub-endpoint", choices=["https://huggingface.co", "https://hf-mirror.com"], default="https://huggingface.co")
    s = sub.add_parser("serve")
    s.add_argument("--index", required=True)
    s.add_argument("--port", type=int, default=9891)
    s.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    args = parser.parse_args()
    if args.command == "build":
        os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"  # Public model; never send saved credentials to a mirror.
        os.environ["HF_ENDPOINT"] = args.hub_endpoint
        if not 1 <= args.batch_size <= 512:
            parser.error("batch-size must be 1..512")
        build(args)
    else:
        if not 1024 <= args.port <= 65535:
            parser.error("port must be 1024..65535")
        serve(args)

if __name__ == "__main__":
    main()
