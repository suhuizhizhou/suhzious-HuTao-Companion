"""Import full mission pages as a supplemental, lossless dialogue archive.

These pages never overwrite TextMap-backed lines. They retain the complete raw
page and a line-by-line view, then link pages to local chapters by exact titles.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
SPEAKER_LINE = re.compile(r"^(?P<speaker>[^：\n]{1,30})：(?P<text>.*)$")


def revision(path: Path) -> str:
    try:
        return subprocess.check_output(
            ["git", "-C", str(path), "rev-parse", "HEAD"],
            text=True,
            encoding="utf-8",
            stderr=subprocess.DEVNULL,
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def normalize(value: str) -> str:
    return re.sub(r"[\s「」『』《》·・：:（）()\-—_]+", "", value).strip()


def load_chapter_titles(
    dialogue_root: Path,
) -> tuple[dict[int, dict], dict[str, set[int]], Counter[str]]:
    chapters: dict[int, dict] = {}
    title_to_chapters: dict[str, set[int]] = defaultdict(set)
    for chapter_path in dialogue_root.glob("*.jsonl"):
        main_id = int(chapter_path.stem)
        main_title = chapter_title = ""
        subquest_titles: set[str] = set()
        with chapter_path.open("r", encoding="utf-8") as handle:
            for raw_line in handle:
                line = json.loads(raw_line)
                main_title = main_title or line.get("main_title", "")
                chapter_title = chapter_title or line.get("chapter_title", "")
                if line.get("subquest_title"):
                    subquest_titles.add(line["subquest_title"])
        chapters[main_id] = {
            "main_title": main_title,
            "chapter_title": chapter_title,
            "subquest_titles": sorted(subquest_titles),
        }
        for title in (main_title, chapter_title):
            key = normalize(title)
            if len(key) >= 4:
                title_to_chapters[key].add(main_id)
    subquest_frequency: Counter[str] = Counter()
    for metadata in chapters.values():
        subquest_frequency.update({
            normalize(value)
            for value in metadata["subquest_titles"]
            if len(normalize(value)) >= 4
        })
    return chapters, title_to_chapters, subquest_frequency


def map_chapters(
    page_name: str,
    raw_text: str,
    chapters: dict[int, dict],
    title_to_chapters: dict[str, set[int]],
    subquest_frequency: Counter[str],
) -> list[int]:
    normalized_name = normalize(page_name)
    mapped: set[int] = set()
    for title, main_ids in title_to_chapters.items():
        if title in normalized_name:
            mapped.update(main_ids)
    if mapped:
        return sorted(mapped)

    # A page may use the series name while the local chapter uses task titles.
    # Use multiple rare, exact subquest headings as anchors. Common headings
    # such as “与派蒙对话” are intentionally excluded by the frequency limit.
    raw_lines = {normalize(line) for line in raw_text.splitlines() if line.strip()}
    for main_id, metadata in chapters.items():
        main_title = normalize(metadata["main_title"])
        sub_titles = {
            normalize(value) for value in metadata["subquest_titles"] if len(normalize(value)) >= 3
        }
        rare_matches = {
            value for value in sub_titles
            if value in raw_lines and subquest_frequency.get(value, 0) <= 3
        }
        title_anchor = len(main_title) >= 4 and (
            main_title in normalized_name or main_title in raw_lines
        )
        if len(rare_matches) >= 2 or (title_anchor and rare_matches):
            mapped.add(main_id)
    return sorted(mapped)


def parse_lines(raw_text: str) -> list[dict]:
    result = []
    for sequence, raw_line in enumerate(raw_text.splitlines()):
        value = raw_line.strip()
        if not value:
            continue
        match = SPEAKER_LINE.match(value)
        if match:
            kind = "dialogue"
            speaker = match.group("speaker").strip()
            text = match.group("text").strip()
        elif (value.startswith("（") and value.endswith("）")) or (
            value.startswith("(") and value.endswith(")")
        ):
            kind, speaker, text = "action", "", value
        else:
            kind, speaker, text = "narration", "", value
        result.append({
            "sequence": sequence,
            "kind": kind,
            "speaker": speaker,
            "text": text,
            "raw_text": raw_line,
        })
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--source",
        type=Path,
        default=REPO_ROOT / ".tmp/genshin-story-data",
    )
    parser.add_argument(
        "--dialogue-root",
        type=Path,
        default=REPO_ROOT / "data/story/dialogue/chapters",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=REPO_ROOT / "data/story/dialogue/pages",
    )
    args = parser.parse_args()

    source_file = args.source / "export/mission_filtered.json"
    if not source_file.is_file():
        raise FileNotFoundError(f"missing mission archive: {source_file}")
    pages = json.loads(source_file.read_text(encoding="utf-8"))
    chapters, title_to_chapters, subquest_frequency = load_chapter_titles(
        args.dialogue_root
    )

    records_root = args.output / "records"
    records_root.mkdir(parents=True, exist_ok=True)
    for stale in records_root.glob("*.json"):
        stale.unlink()

    manifest_pages = []
    mapped_page_count = mapped_chapter_links = total_lines = dialogue_lines = 0
    for page in pages:
        page_id = str(page.get("id", "")).strip()
        page_name = str(page.get("name", "")).strip()
        raw_text = str(page.get("text", ""))
        if not page_id or not raw_text:
            continue
        chapter_ids = map_chapters(
            page_name,
            raw_text,
            chapters,
            title_to_chapters,
            subquest_frequency,
        )
        lines = parse_lines(raw_text)
        record = {
            "version": 1,
            "page_id": page_id,
            "name": page_name,
            "chapter_ids": [f"main-quest-{value}" for value in chapter_ids],
            "source": {
                "repository": "https://github.com/hbprotoss/genshin-story-data",
                "revision": revision(args.source),
                "record_id": page_id,
                "kind": "official-community-page-export",
            },
            "raw_text": raw_text,
            "lines": lines,
        }
        output_file = records_root / f"{page_id}.json"
        output_file.write_text(
            json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n",
            encoding="utf-8",
        )
        page_dialogue_lines = sum(line["kind"] == "dialogue" for line in lines)
        total_lines += len(lines)
        dialogue_lines += page_dialogue_lines
        if chapter_ids:
            mapped_page_count += 1
            mapped_chapter_links += len(chapter_ids)
        manifest_pages.append({
            "page_id": page_id,
            "name": page_name,
            "chapter_ids": record["chapter_ids"],
            "line_count": len(lines),
            "dialogue_line_count": page_dialogue_lines,
            "file": f"records/{page_id}.json",
        })

    manifest = {
        "version": 1,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "source": {
            "repository": "https://github.com/hbprotoss/genshin-story-data",
            "revision": revision(args.source),
            "file": "export/mission_filtered.json",
            "notice": "Supplemental page archive; never overwrites TextMap-backed lines.",
        },
        "page_count": len(manifest_pages),
        "mapped_page_count": mapped_page_count,
        "mapped_chapter_link_count": mapped_chapter_links,
        "line_count": total_lines,
        "dialogue_line_count": dialogue_lines,
        "pages": manifest_pages,
    }
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    coverage_path = REPO_ROOT / "data/story/coverage.json"
    if coverage_path.exists():
        coverage = json.loads(coverage_path.read_text(encoding="utf-8"))
        coverage["supplemental_page_corpus"] = {
            "page_count": len(manifest_pages),
            "mapped_page_count": mapped_page_count,
            "mapped_chapter_link_count": mapped_chapter_links,
            "line_count": total_lines,
            "dialogue_line_count": dialogue_lines,
            "source_repository": manifest["source"]["repository"],
            "source_revision": manifest["source"]["revision"],
            "guarantee": (
                "完整 raw_text 与逐行顺序同时保留；仅作为补充档案，"
                "不覆盖 TextMap 台词，不伪造游戏 line_id。"
            ),
        }
        coverage_path.write_text(
            json.dumps(coverage, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    print(f"pages: {len(manifest_pages)} (mapped {mapped_page_count})")
    print(f"chapter links: {mapped_chapter_links}")
    print(f"lines: {total_lines} (dialogue {dialogue_lines})")


if __name__ == "__main__":
    main()
