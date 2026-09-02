"""Report how many unresolved dialogue hashes each historical TextMap can fill."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]


def version_key(path: Path) -> tuple[int, ...]:
    try:
        return tuple(int(part) for part in path.stem.split("."))
    except ValueError:
        return (9999,)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--dialogue-root",
        type=Path,
        default=REPO_ROOT / "data/story/dialogue/chapters",
    )
    parser.add_argument(
        "--text-map-root",
        type=Path,
        default=REPO_ROOT / ".tmp/story-textmaps",
    )
    args = parser.parse_args()

    missing_hashes: set[str] = set()
    missing_line_count = 0
    zero_hash_line_count = 0
    for chapter_path in args.dialogue_root.glob("*.jsonl"):
        with chapter_path.open("r", encoding="utf-8") as handle:
            for raw_line in handle:
                line = json.loads(raw_line)
                if line.get("resolved"):
                    continue
                missing_line_count += 1
                if line.get("text_hash"):
                    missing_hashes.add(str(line["text_hash"]))
                else:
                    zero_hash_line_count += 1

    covered: set[str] = set()
    print(f"missing lines: {missing_line_count}")
    print(f"missing lines with zero hash: {zero_hash_line_count}")
    print(f"unique missing hashes: {len(missing_hashes)}")
    numeric_hashes = {int(value) for value in missing_hashes}
    alternate_hashes: dict[str, str] = {}
    for value in numeric_hashes:
        unsigned = value & 0xFFFFFFFF
        signed = unsigned - 0x100000000 if unsigned >= 0x80000000 else unsigned
        alternate_hashes[str(unsigned)] = str(value)
        alternate_hashes[str(signed)] = str(value)
    for text_map_path in sorted(args.text_map_root.glob("*.json"), key=version_key):
        with text_map_path.open("r", encoding="utf-8-sig") as handle:
            keys = set(json.load(handle))
        available = keys & missing_hashes
        alternate = (keys & set(alternate_hashes)) - missing_hashes
        incremental = available - covered
        covered.update(available)
        print(
            f"{text_map_path.stem:>6}: direct={len(available):>5} "
            f"incremental={len(incremental):>5} alternate32={len(alternate):>5}"
        )
    print(f"total uniquely recoverable hashes: {len(covered)}")


if __name__ == "__main__":
    main()
