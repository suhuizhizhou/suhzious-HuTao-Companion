"""Build a lossless, chapter-addressable dialogue store from CodexQuest dumps.

The summary corpus is only a router. This importer preserves every dialogue
variant found in CodexQuest, including raw text, cleaned display text, speaker,
text hashes, line ids, ordering and source file, so runtime retrieval can quote
the actual conversation after selecting a small number of chapters.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from collections import Counter, defaultdict
from collections.abc import Iterator
from datetime import datetime, timezone
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
TAG = re.compile(r"<[^>]+>")


def load_json(path: Path):
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def revision(path: Path) -> str:
    try:
        return subprocess.check_output(
            ["git", "-C", str(path), "rev-parse", "HEAD"],
            text=True, encoding="utf-8", stderr=subprocess.DEVNULL,
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def resolve(text_map: dict[str, str], value) -> str:
    return text_map.get(str(value), "") if value else ""


def display_text(raw: str) -> str:
    value = raw.replace("{NICKNAME}", "旅行者").replace("\\n", "\n")
    value = TAG.sub("", value)
    return value.strip()


def marker_hash(value, marker: str) -> int:
    if isinstance(value, dict):
        if value.get("JOBGILDNLEL") == marker:
            return int(value.get("BNJEGIAOKGM", 0))
        for child in value.values():
            found = marker_hash(child, marker)
            if found:
                return found
    elif isinstance(value, list):
        for child in value:
            found = marker_hash(child, marker)
            if found:
                return found
    return 0


def dialogue_nodes(value) -> Iterator[dict]:
    if isinstance(value, dict):
        if "NDANANGPLHB" in value and "OFKGPGLHIDJ" in value:
            yield value
            return
        for child in value.values():
            yield from dialogue_nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from dialogue_nodes(child)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=REPO_ROOT / ".tmp/AnimeGameData")
    parser.add_argument(
        "--historical-source", type=Path,
        default=REPO_ROOT / ".tmp/GenshinDataHistory",
    )
    parser.add_argument(
        "--extra-text-map", type=Path, action="append", default=[],
        help=(
            "额外的历史 TextMapCHS.json，可重复指定；按命令行顺序合并，"
            "只用于补齐缺失键，当前版本 TextMap 始终拥有最高优先级"
        ),
    )
    parser.add_argument(
        "--output", type=Path,
        default=REPO_ROOT / "data/story/dialogue",
    )
    args = parser.parse_args()

    codex_root = args.source / "BinOutput/CodexQuest"
    current_map = load_json(args.source / "TextMap/TextMapCHS.json")
    historical_map_path = args.historical_source / "TextMap/TextMapCHS.json"
    text_map = load_json(historical_map_path) if historical_map_path.exists() else {}
    extra_text_maps = []
    for extra_path in args.extra_text_map:
        if not extra_path.is_file():
            raise FileNotFoundError(f"missing extra TextMap: {extra_path}")
        # 后指定的快照可以覆盖更早快照；当前版本仍在最后统一覆盖。
        text_map.update(load_json(extra_path))
        resolved_extra_path = extra_path.resolve()
        try:
            recorded_path = resolved_extra_path.relative_to(REPO_ROOT).as_posix()
        except ValueError:
            recorded_path = str(resolved_extra_path)
        extra_text_maps.append(recorded_path)
    text_map.update(current_map)
    if not codex_root.is_dir():
        raise FileNotFoundError(f"missing CodexQuest directory: {codex_root}")

    summary_by_id = {}
    summary_path = REPO_ROOT / "data/story/chapters.jsonl"
    if summary_path.exists():
        for line in summary_path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                item = json.loads(line)
                summary_by_id[int(item["id"].rsplit("-", 1)[1])] = item

    quest_rows = load_json(args.source / "ExcelBinOutput/QuestExcelConfigData.json")
    historical_quest_path = args.historical_source / "ExcelBinOutput/QuestExcelConfigData.json"
    if historical_quest_path.exists():
        quest_rows = [*load_json(historical_quest_path), *quest_rows]
    subquest_meta = {}
    for item in quest_rows:
        sub_id = int(item.get("subId", 0))
        if not sub_id:
            continue
        title_hash = int(item.get("stepDescTextMapHash", 0))
        title = display_text(resolve(text_map, title_hash))
        existing = subquest_meta.get(sub_id, {})
        subquest_meta[sub_id] = {
            "main_id": int(item.get("mainId", existing.get("main_id", 0))),
            "title": title or existing.get("title", ""),
        }

    npc_rows = []
    historical_npc_path = args.historical_source / "ExcelBinOutput/NpcExcelConfigData.json"
    if historical_npc_path.exists():
        npc_rows.extend(load_json(historical_npc_path))
    npc_rows.extend(load_json(args.source / "ExcelBinOutput/NpcExcelConfigData.json"))
    npc_meta = {}
    for item in npc_rows:
        npc_id = int(item.get("id", 0))
        candidate_hash = int(item.get("nameTextMapHash", 0))
        if not npc_id or not candidate_hash:
            continue
        existing_hash = npc_meta.get(npc_id, 0)
        if not existing_hash or (
            not resolve(text_map, existing_hash) and resolve(text_map, candidate_hash)
        ):
            npc_meta[npc_id] = candidate_hash

    def resolve_role(role: dict) -> tuple[str, int, str]:
        role_type = str(role.get("_type", "TALK_ROLE_UNKNOWN"))
        role_id_text = str(role.get("_id", ""))
        role_id = int(role_id_text) if role_id_text.isdigit() else 0
        if role_type == "TALK_ROLE_PLAYER":
            return role_type, 0, "旅行者"
        speaker_hash = npc_meta.get(role_id, 0)
        speaker = display_text(resolve(text_map, speaker_hash))
        if not speaker and role_id:
            speaker = f"未解析角色#{role_id}"
        return role_type, speaker_hash, speaker

    talk_role_cache: dict[int, dict[int, tuple[str, int, str]]] = {}

    def role_for_line(line_id: int) -> tuple[str, int, str] | None:
        talk_id = line_id // 100
        if talk_id not in talk_role_cache:
            roles = {}
            for talk_kind in ("Quest", "Activity", "Coop"):
                path = args.source / f"BinOutput/Talk/{talk_kind}/{talk_id}.json"
                if not path.exists():
                    continue
                talk = load_json(path)
                for talk_node in talk.get("PFALHAKIILD", []):
                    candidate_id = int(talk_node.get("OIFGMOHKPOI", 0))
                    roles[candidate_id] = resolve_role(talk_node.get("LFGCLNLPAPB", {}))
                break
            talk_role_cache[talk_id] = roles
        return talk_role_cache[talk_id].get(line_id)

    chapters_root = args.output / "chapters"
    chapters_root.mkdir(parents=True, exist_ok=True)
    for stale in chapters_root.glob("*.jsonl"):
        stale.unlink()

    chapter_entries = []
    total_lines = resolved_lines = missing_lines = 0
    parse_errors = []
    for source_file in sorted(codex_root.glob("*.json")):
        try:
            root = load_json(source_file)
            main_id = int(root.get("IMJHJGBNMMD", 0))
            if not main_id:
                continue
            main_title_hash = marker_hash(root.get("HEDPNHPBMJH", {}), "MainQuestTitle")
            main_desc_hash = marker_hash(root.get("CBLIMBBBKNK", {}), "MainQuestDesp")
            chapter_title_hash = marker_hash(root.get("ALOHJMPDFKI", {}), "ChapterTitle")
            chapter_num_hash = marker_hash(root.get("NNPJABOAJPL", {}), "ChapterNum")
            main_title = display_text(resolve(text_map, main_title_hash))
            main_desc = display_text(resolve(text_map, main_desc_hash))
            chapter_title = display_text(resolve(text_map, chapter_title_hash))
            chapter_num = display_text(resolve(text_map, chapter_num_hash))

            output_file = chapters_root / f"{main_id}.jsonl"
            line_count = resolved_count = 0
            with output_file.open("w", encoding="utf-8", newline="\n") as handle:
                for subquest_index, subquest in enumerate(root.get("EBNBLBEIFFJ", [])):
                    sub_title_hash = marker_hash(subquest, "SubQuestTitle")
                    sub_title = display_text(resolve(text_map, sub_title_hash))
                    for sequence, node in enumerate(dialogue_nodes(subquest)):
                        speaker_type = "SpeakerPlayer" if marker_hash(node, "SpeakerPlayer") else "SpeakerKnown"
                        speaker_hash = marker_hash(node, speaker_type)
                        speaker_raw = resolve(text_map, speaker_hash)
                        speaker = "旅行者" if speaker_type == "SpeakerPlayer" else display_text(speaker_raw)
                        variants = node.get("OFKGPGLHIDJ", [])
                        for variant, dialog in enumerate(variants):
                            text_hash = marker_hash(dialog, "DialogNormal")
                            raw_text = resolve(text_map, text_hash)
                            text = display_text(raw_text)
                            line_id = int(dialog.get("AAICCGABILO", 0))
                            if not speaker:
                                recovered = role_for_line(line_id)
                                if recovered is not None:
                                    speaker_type, speaker_hash, speaker = recovered
                            record = {
                                "chapter_id": f"main-quest-{main_id}",
                                "main_id": main_id,
                                "main_title": main_title,
                                "main_description": main_desc,
                                "chapter_title": chapter_title,
                                "chapter_number": chapter_num,
                                "subquest_index": subquest_index,
                                "subquest_title": sub_title,
                                "sequence": sequence,
                                "variant": variant,
                                "line_id": line_id,
                                "speaker_type": speaker_type,
                                "speaker_text_hash": speaker_hash,
                                "speaker": speaker,
                                "speaker_raw": speaker_raw,
                                "text_hash": text_hash,
                                "text": text,
                                "raw_text": raw_text,
                                "resolved": bool(raw_text),
                                "source_file": f"BinOutput/CodexQuest/{source_file.name}",
                            }
                            handle.write(json.dumps(
                                record, ensure_ascii=False, separators=(",", ":")
                            ) + "\n")
                            line_count += 1
                            if raw_text:
                                resolved_count += 1

            if line_count == 0:
                output_file.unlink(missing_ok=True)
                continue
            total_lines += line_count
            resolved_lines += resolved_count
            missing_lines += line_count - resolved_count
            chapter_entries.append({
                "chapter_id": f"main-quest-{main_id}",
                "main_id": main_id,
                "title": main_title,
                "chapter_title": chapter_title,
                "line_count": line_count,
                "resolved_line_count": resolved_count,
                "file": f"chapters/{main_id}.jsonl",
                "source_file": f"BinOutput/CodexQuest/{source_file.name}",
                "source_kind": "codex_quest",
            })
        except Exception as exc:  # keep the rest of the corpus buildable
            parse_errors.append({"file": source_file.name, "error": str(exc)})

    # CodexQuest is preferred because it already preserves curated sequence and branches.
    # For chapters not present there, fall back to the raw Talk/Quest graph.
    codex_main_ids = {entry["main_id"] for entry in chapter_entries}
    talk_by_main: dict[int, list[tuple[str, int, Path]]] = defaultdict(list)
    for talk_kind in ("Quest", "Activity", "Coop"):
        talk_root = args.source / f"BinOutput/Talk/{talk_kind}"
        if not talk_root.is_dir():
            continue
        for talk_file in talk_root.glob("*.json"):
            if not talk_file.stem.isdigit():
                continue
            sub_id = int(talk_file.stem)
            main_id = int(subquest_meta.get(sub_id, {}).get("main_id", 0))
            if main_id and main_id not in codex_main_ids and main_id in summary_by_id:
                talk_by_main[main_id].append((talk_kind, sub_id, talk_file))

    for main_id, talk_files in sorted(talk_by_main.items()):
        output_file = chapters_root / f"{main_id}.jsonl"
        line_count = resolved_count = 0
        source_files = []
        try:
            with output_file.open("w", encoding="utf-8", newline="\n") as handle:
                for subquest_index, (talk_kind, sub_id, talk_file) in enumerate(sorted(talk_files)):
                    relative_source = f"BinOutput/Talk/{talk_kind}/{talk_file.name}"
                    source_files.append(relative_source)
                    talk = load_json(talk_file)
                    talk_id = int(talk.get("IOKNFDJFGDH", sub_id))
                    sub_title = subquest_meta.get(sub_id, {}).get("title", "")
                    for sequence, node in enumerate(talk.get("PFALHAKIILD", [])):
                        text_hash = int(node.get("OACNIBLFFDI", 0))
                        raw_text = resolve(text_map, text_hash)
                        text = display_text(raw_text)
                        role = node.get("LFGCLNLPAPB", {})
                        role_id_text = str(role.get("_id", ""))
                        role_id = int(role_id_text) if role_id_text.isdigit() else 0
                        role_type, speaker_hash, speaker = resolve_role(role)
                        record = {
                            "chapter_id": f"main-quest-{main_id}",
                            "main_id": main_id,
                            "main_title": summary_by_id[main_id].get("title", ""),
                            "main_description": summary_by_id[main_id].get("summary", ""),
                            "chapter_title": summary_by_id[main_id].get("chapter", ""),
                            "chapter_number": summary_by_id[main_id].get("act", ""),
                            "subquest_index": subquest_index,
                            "subquest_id": sub_id,
                            "subquest_title": sub_title,
                            "talk_id": talk_id,
                            "sequence": sequence,
                            "variant": 0,
                            "line_id": int(node.get("OIFGMOHKPOI", 0)),
                            "next_line_ids": [int(value) for value in node.get("KMLAFCBMFEI", [])],
                            "speaker_type": role_type,
                            "speaker_id": role_id,
                            "speaker_text_hash": speaker_hash,
                            "speaker": speaker,
                            "speaker_raw": speaker,
                            "text_hash": text_hash,
                            "text": text,
                            "raw_text": raw_text,
                            "resolved": bool(raw_text),
                            "source_file": relative_source,
                        }
                        handle.write(json.dumps(
                            record, ensure_ascii=False, separators=(",", ":")
                        ) + "\n")
                        line_count += 1
                        if raw_text:
                            resolved_count += 1
            if line_count == 0:
                output_file.unlink(missing_ok=True)
                continue
            total_lines += line_count
            resolved_lines += resolved_count
            missing_lines += line_count - resolved_count
            chapter_entries.append({
                "chapter_id": f"main-quest-{main_id}",
                "main_id": main_id,
                "title": summary_by_id[main_id].get("title", ""),
                "chapter_title": summary_by_id[main_id].get("chapter", ""),
                "line_count": line_count,
                "resolved_line_count": resolved_count,
                "file": f"chapters/{main_id}.jsonl",
                "source_files": source_files,
                "source_kind": "talk_quest_fallback",
            })
        except Exception as exc:
            output_file.unlink(missing_ok=True)
            parse_errors.append({"file": f"Talk/Quest main_id={main_id}", "error": str(exc)})

    manifest = {
        "version": 1,
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "source": {
            "repository": "https://github.com/DimbreathBot/AnimeGameData",
            "revision": revision(args.source),
            "historical_text_repository": "https://github.com/Sycamore0/GenshinData",
            "historical_text_revision": revision(args.historical_source),
            "extra_text_maps": extra_text_maps,
        },
        "storage": "one lossless JSONL file per main quest; raw_text is retained verbatim",
        "inputs": [
            "BinOutput/CodexQuest",
            "BinOutput/Talk/Quest",
            "BinOutput/Talk/Activity",
            "BinOutput/Talk/Coop",
            "ExcelBinOutput/QuestExcelConfigData.json",
            "ExcelBinOutput/NpcExcelConfigData.json",
            "TextMap/TextMapCHS.json",
        ],
        "chapter_count": len(chapter_entries),
        "line_count": total_lines,
        "resolved_line_count": resolved_lines,
        "missing_text_line_count": missing_lines,
        "parse_errors": parse_errors,
        "source_kind_counts": dict(Counter(
            entry["source_kind"] for entry in chapter_entries
        )),
        "chapters": chapter_entries,
    }
    (args.output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    coverage_path = REPO_ROOT / "data/story/coverage.json"
    if coverage_path.exists():
        coverage = load_json(coverage_path)
        summary_count = int(coverage.get("chapter_count", len(summary_by_id)))
        coverage["dialogue_corpus"] = {
            "chapter_count": len(chapter_entries),
            "summary_chapter_count": summary_count,
            "chapter_coverage_ratio": round(
                len(chapter_entries) / summary_count, 4
            ) if summary_count else 0,
            "line_count": total_lines,
            "resolved_line_count": resolved_lines,
            "missing_text_line_count": missing_lines,
            "parse_error_count": len(parse_errors),
            "source_kind_counts": manifest["source_kind_counts"],
            "guarantee": (
                "raw_text、text_hash、line_id、说话人、顺序与分支均保留；"
                "无法从当前及历史 TextMap 解析的行仍保留哈希与结构，不伪造文本。"
            ),
        }
        coverage_path.write_text(
            json.dumps(coverage, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    print(f"dialogue chapters: {len(chapter_entries)}")
    print(f"dialogue lines: {total_lines} (resolved {resolved_lines}, missing {missing_lines})")
    print(f"parse errors: {len(parse_errors)}")


if __name__ == "__main__":
    main()
