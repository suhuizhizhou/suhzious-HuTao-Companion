"""Build the local story vector index from verified chapter summaries.

Uses only Python's standard library. The hashing algorithm and tokenization are
kept in sync with StoryVectorStore.cs, so runtime retrieval stays offline.
"""

from __future__ import annotations

import argparse
import json
import math
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path


def is_cjk(ch: str) -> bool:
    return "\u3400" <= ch <= "\u9fff"


def tokens(text: str):
    normalized = "".join(
        ch.lower() for ch in text.strip()
        if ch.isalnum() or is_cjk(ch) or ch.isspace()
    )
    for word in normalized.split():
        if any(not is_cjk(ch) for ch in word):
            yield "w:" + word
    cjk = "".join(ch for ch in normalized if is_cjk(ch))
    for size in (1, 2, 3):
        for pos in range(len(cjk) - size + 1):
            yield f"c{size}:" + cjk[pos:pos + size]


def fnv1a(value: str) -> int:
    result = 2166136261
    for byte in value.encode("utf-8"):
        result ^= byte
        result = (result * 16777619) & 0xFFFFFFFF
    return result


def source_text(chapter: dict) -> str:
    fields = [
        chapter.get("region", ""), chapter.get("chapter", ""),
        chapter.get("act", ""), chapter.get("title", ""),
        chapter.get("summary", ""),
    ]
    fields += chapter.get("key_events", [])
    fields += chapter.get("characters", [])
    fields += chapter.get("keywords", [])
    # Titles, names and explicit keywords deserve more retrieval weight.
    fields += [chapter.get("title", "")] * 2
    fields += chapter.get("characters", [])
    fields += chapter.get("keywords", [])
    return " ".join(str(value) for value in fields if value)


def read_jsonl(path: Path) -> list[dict]:
    documents = []
    seen = set()
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        item = json.loads(line)
        if not item.get("id") or not item.get("summary"):
            raise ValueError(f"line {number}: id and summary are required")
        if item["id"] in seen:
            raise ValueError(f"line {number}: duplicate id {item['id']}")
        seen.add(item["id"])
        documents.append(item)
    return documents


def main() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, default=repo_root / "data/story/chapters.jsonl")
    parser.add_argument("--output", type=Path, default=repo_root / "data/story/index.json")
    parser.add_argument("--dimension", type=int, default=4096)
    args = parser.parse_args()

    documents = read_jsonl(args.input)
    counts = [Counter(fnv1a(token) % args.dimension for token in tokens(source_text(doc)))
              for doc in documents]
    document_frequency = Counter(bucket for vector in counts for bucket in vector)
    idf = {
        bucket: math.log((1 + len(documents)) / (1 + frequency)) + 1
        for bucket, frequency in document_frequency.items()
    }

    indexed = []
    for document, count in zip(documents, counts):
        vector = {bucket: (1 + math.log(tf)) * idf[bucket] for bucket, tf in count.items()}
        norm = math.sqrt(sum(value * value for value in vector.values())) or 1
        indexed.append({**document, "vector": {str(k): v / norm for k, v in vector.items()}})

    payload = {
        "version": 1,
        "dimension": args.dimension,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "idf": {str(key): value for key, value in idf.items()},
        "documents": indexed,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"indexed {len(indexed)} chapters -> {args.output}")


if __name__ == "__main__":
    main()
