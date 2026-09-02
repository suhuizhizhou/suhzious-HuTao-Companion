"""Convert AnimeGameData quest dumps into chapter-level story records.

The source dump remains outside version control. This script keeps only the
official Chinese quest title, synopsis and a bounded set of objective texts,
then emits the JSONL consumed by build_story_index.py.
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
TYPE_NAMES = {
    "AQ": "魔神任务",
    "LQ": "传说任务",
    "WQ": "世界任务",
    "EQ": "活动剧情",
    "IQ": "其他剧情任务",
}
REGION_HINTS = {
    "蒙德": ("蒙德", "风神", "温迪", "西风骑士团", "风龙废墟", "龙脊雪山"),
    "璃月": ("璃月", "岩神", "钟离", "往生堂", "绝云间", "层岩巨渊", "沉玉谷"),
    "稻妻": ("稻妻", "雷神", "雷电影", "鸣神岛", "海祇岛", "渊下宫"),
    "须弥": ("须弥", "草神", "纳西妲", "教令院", "雨林", "沙漠"),
    "枫丹": ("枫丹", "水神", "芙宁娜", "那维莱特", "梅洛彼得堡"),
    "纳塔": ("纳塔", "火神", "玛薇卡", "夜神之国"),
    "挪德卡莱": ("挪德卡莱",),
    "至冬": ("至冬", "愚人众", "冰之女皇"),
    "坎瑞亚": ("坎瑞亚", "戴因斯雷布", "深渊教团"),
}
CITY_NAMES = {
    1: "蒙德", 2: "璃月", 3: "稻妻", 4: "须弥",
    5: "枫丹", 6: "纳塔", 7: "挪德卡莱",
}
ICON_CHARACTERS = {
    "Albedo": "阿贝多", "Alhatham": "艾尔海森", "Ambor": "安柏",
    "Arlecchino": "阿蕾奇诺", "Ayaka": "神里绫华", "Ayato": "神里绫人",
    "Baizhuer": "白术", "Chiori": "千织", "Clorinde": "克洛琳德",
    "Cyno": "赛诺", "Dehya": "迪希雅", "Diluc": "迪卢克", "Durin": "杜林",
    "Emilie": "艾梅莉埃", "Escoffier": "爱可菲", "Eula": "优菈",
    "Furina": "芙宁娜", "Ganyu": "甘雨", "Hutao": "胡桃",
    "Itto": "荒泷一斗", "Kaeya": "凯亚", "Kazuha": "枫原万叶",
    "Klee": "可莉", "Kokomi": "珊瑚宫心海", "Liney": "林尼",
    "Linnea": "琳妮特", "Lisa": "丽莎", "Liuyun": "闲云",
    "Mavuika": "玛薇卡", "Mizuki": "梦见月瑞希", "Mona": "莫娜",
    "Nahida": "纳西妲", "Navia": "娜维娅", "Neuvillette": "那维莱特",
    "Nilou": "妮露", "QIN": "琴", "Razor": "雷泽", "Shougun": "雷电将军",
    "Sigewinne": "希格雯", "SkirkNew": "丝柯克", "Tartaglia": "达达利亚",
    "Tighnari": "提纳里", "Varka": "法尔伽", "Venti": "温迪",
    "Wriothesley": "莱欧斯利", "Xiangling": "香菱", "Xiao": "魈",
    "Xingqiu": "行秋", "Yae": "八重神子", "Yelan": "夜兰",
    "Yoimiya": "宵宫", "Zhongli": "钟离",
}
BAD_TITLE = re.compile(
    r"(?:\$HIDDEN|\(test\)|（test）|测试|test quest|废弃|占位)", re.IGNORECASE
)
TAG = re.compile(r"<[^>]+>")
CONTROL = re.compile(r"\{[^{}]*(?:SEXPRO|REALNAME|NICKNAME)[^{}]*\}")


def load_json(path: Path):
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def clean(value: str, limit: int) -> str:
    value = value.replace("{NICKNAME}", "旅行者").replace("\\n", " ")
    value = CONTROL.sub("旅行者", value)
    value = TAG.sub("", value)
    value = re.sub(r"\s+", " ", value).strip()
    if len(value) <= limit:
        return value
    return value[: limit - 1].rstrip("，。；; ") + "…"


def resolve(text_map: dict[str, str], value) -> str:
    if value is None:
        return ""
    return text_map.get(str(value), "").strip()


def infer_region(text: str) -> str:
    scores = {
        region: sum(text.count(hint) for hint in hints)
        for region, hints in REGION_HINTS.items()
    }
    best, score = max(scores.items(), key=lambda pair: pair[1])
    return best if score else "提瓦特"


def source_revision(source_root: Path) -> str:
    try:
        return subprocess.check_output(
            ["git", "-C", str(source_root), "rev-parse", "HEAD"],
            text=True, encoding="utf-8", stderr=subprocess.DEVNULL,
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--source", type=Path,
        default=REPO_ROOT / ".tmp/AnimeGameData",
        help="local checkout of DimbreathBot/AnimeGameData",
    )
    parser.add_argument(
        "--output", type=Path,
        default=REPO_ROOT / "data/story/chapters.jsonl",
    )
    parser.add_argument(
        "--historical-text-map", type=Path,
        default=REPO_ROOT / ".tmp/GenshinDataHistory/TextMap/TextMapCHS.json",
        help="optional older TextMap used to restore quest text removed from the current release",
    )
    parser.add_argument(
        "--coverage", type=Path,
        default=REPO_ROOT / "data/story/coverage.json",
    )
    parser.add_argument("--max-events", type=int, default=10)
    args = parser.parse_args()

    main_path = args.source / "ExcelBinOutput/MainQuestExcelConfigData.json"
    quest_path = args.source / "ExcelBinOutput/QuestExcelConfigData.json"
    chapter_path = args.source / "ExcelBinOutput/ChapterExcelConfigData.json"
    text_path = args.source / "TextMap/TextMapCHS.json"
    for path in (main_path, quest_path, chapter_path, text_path):
        if not path.exists():
            raise FileNotFoundError(f"missing source file: {path}")

    main_quests = load_json(main_path)
    sub_quests = load_json(quest_path)
    chapters = load_json(chapter_path)
    text_map = load_json(text_path)
    historical_revision = ""
    historical_text_map = {}
    historical_main_by_id = {}
    historical_sub_quests = []
    if args.historical_text_map.exists():
        historical_root = args.historical_text_map.parents[1]
        historical_text_map = load_json(args.historical_text_map)
        historical_main_path = historical_root / "ExcelBinOutput/MainQuestExcelConfigData.json"
        historical_quest_path = historical_root / "ExcelBinOutput/QuestExcelConfigData.json"
        if historical_main_path.exists():
            historical_main_by_id = {
                int(item.get("id", 0)): item for item in load_json(historical_main_path)
            }
        if historical_quest_path.exists():
            historical_sub_quests = load_json(historical_quest_path)
        historical_revision = source_revision(historical_root)
    revision = source_revision(args.source)

    chapter_by_id = {int(item.get("id", 0)): item for item in chapters}
    chapter_by_main = {}
    for item in chapters:
        for main_id in item.get("PACJEJCGPLN", []):
            chapter_by_main[int(main_id)] = item

    steps_by_main: dict[int, list[tuple[int, str]]] = defaultdict(list)
    for source_quests, source_text in (
        (historical_sub_quests, historical_text_map),
        (sub_quests, text_map),
    ):
        for sub in source_quests:
            description = clean(resolve(source_text, sub.get("stepDescTextMapHash")), 180)
            if description and not BAD_TITLE.search(description):
                steps_by_main[int(sub.get("mainId", 0))].append(
                    (int(sub.get("order", 0)), description)
                )

    records = []
    skipped = Counter()
    scope_counts = Counter()
    region_counts = Counter()
    for main in main_quests:
        quest_id = int(main.get("id", 0))
        historical_main = historical_main_by_id.get(quest_id, {})
        title = clean(resolve(text_map, main.get("titleTextMapHash")), 100)
        summary = clean(resolve(text_map, main.get("descTextMapHash")), 600)
        used_history = False
        if not title:
            title = clean(resolve(
                historical_text_map, historical_main.get("titleTextMapHash")
            ), 100)
            used_history = bool(title)
        if not summary:
            summary = clean(resolve(
                historical_text_map, historical_main.get("descTextMapHash")
            ), 600)
            used_history = used_history or bool(summary)
        if not title or not summary:
            skipped["missing_title_or_summary"] += 1
            continue
        if BAD_TITLE.search(title) or BAD_TITLE.search(summary):
            skipped["test_or_placeholder"] += 1
            continue

        raw_type = main.get("type") or "AQ"
        scope = TYPE_NAMES.get(raw_type, f"其他任务（{raw_type}）")
        chapter_meta = chapter_by_main.get(quest_id) or chapter_by_id.get(
            int(main.get("chapterId", 0))
        )
        ordered_steps = sorted(steps_by_main.get(quest_id, []))
        events = []
        for _, event in ordered_steps:
            if event != summary and event not in events:
                events.append(event)
            if len(events) >= max(1, args.max_events):
                break
        inferred_region = infer_region(" ".join([title, summary, *events]))
        region = CITY_NAMES.get(int((chapter_meta or {}).get("cityId", 0)), inferred_region)
        icon = (chapter_meta or {}).get("chapterIcon", "")
        icon_name = icon.removeprefix("UI_ChapterIcon_")
        story_text = " ".join([title, summary, *events])
        characters = list(dict.fromkeys([
            *([ICON_CHARACTERS[icon_name]] if icon_name in ICON_CHARACTERS else []),
            *(name for name in ICON_CHARACTERS.values() if name in story_text),
        ]))
        broad_title = clean(resolve(text_map, (chapter_meta or {}).get("chapterTitleTextMapHash")), 100)
        broad_number = clean(resolve(text_map, (chapter_meta or {}).get("chapterNumTextMapHash")), 60)
        act = " · ".join(value for value in (broad_number, broad_title) if value)
        keywords = list(dict.fromkeys(
            value for value in [title, scope, region, raw_type, *characters, broad_title]
            if value
        ))
        source = (
            "DimbreathBot/AnimeGameData@" + revision[:12] +
            f"; MainQuestExcelConfigData id={quest_id}; TextMapCHS"
        )
        if used_history:
            source += "; historical fallback Sycamore0/GenshinData@" + historical_revision[:12]
        record = {
            "id": f"main-quest-{quest_id}",
            "scope": scope,
            "region": region,
            "chapter": title,
            "act": act,
            "title": title,
            "summary": summary,
            "key_events": events,
            "characters": characters,
            "keywords": keywords,
            "source": source,
            "coverage": "official-quest-text",
        }
        records.append(record)
        scope_counts[scope] += 1
        region_counts[region] += 1

    records.sort(key=lambda item: int(item["id"].rsplit("-", 1)[1]))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as handle:
        for record in records:
            handle.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")

    coverage = {
        "status": "generated_from_official_quest_text",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "chapter_count": len(records),
        "source": {
            "repository": "https://github.com/DimbreathBot/AnimeGameData",
            "revision": revision,
            "files": [
                "ExcelBinOutput/MainQuestExcelConfigData.json",
                "ExcelBinOutput/QuestExcelConfigData.json",
                "ExcelBinOutput/ChapterExcelConfigData.json",
                "TextMap/TextMapCHS.json",
            ],
            "credit": "Data dump maintained by Dimbreath; source README requests attribution.",
        },
        "scope_counts": dict(scope_counts),
        "region_counts": dict(region_counts),
        "skipped": dict(skipped),
        "method": (
            "每个 MainQuest 生成一条章节记录，摘要取官方任务简介，关键事件取前十条去重后的官方步骤描述；"
            "过滤缺少标题/简介以及测试、废弃、占位任务。地区由文本关键词推断，可能为‘提瓦特’。"
        ),
        "limitations": [
            "这是任务级剧情档案，不等同于逐句对话全文。",
            "地区为规则推断字段，回答事实应以标题、简介和关键事件为准。",
            "版本更新后应重新导入并构建索引。",
        ],
    }
    if historical_revision:
        coverage["source"]["historical_text_map"] = {
            "repository": "https://github.com/Sycamore0/GenshinData",
            "revision": historical_revision,
            "purpose": "补回当前版本已移除的早期任务标题与简介，仅在当前 TextMap 缺失时使用。",
        }
    args.coverage.write_text(
        json.dumps(coverage, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"imported {len(records)} quest chapters -> {args.output}")
    print("scope counts:", dict(scope_counts))
    print("skipped:", dict(skipped))


if __name__ == "__main__":
    main()
