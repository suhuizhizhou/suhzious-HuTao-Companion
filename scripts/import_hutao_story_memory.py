"""Extract verified Hu Tao character stories from a local historical TextMap.

Each source string is retained verbatim; paragraph IDs are hash + ordinal.
This is an importer, not an LLM summary generator. No network or game process access.
"""
import argparse
import hashlib
import json
from pathlib import Path

STORIES = {
    "280463848": "角色故事·葬仪与童年",
    "1414437000": "角色故事·经营与客卿",
    "140894640": "角色故事·大咪二咪",
    "821825560": "角色故事·诗友",
    "1964025480": "角色故事·七七",
    "2520842344": "角色故事·帽子",
    "236984664": "神之眼·爷爷与边界",
}

def main():
    root = Path(__file__).resolve().parents[1]
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, default=root / '.tmp/GenshinDataHistory/TextMap/TextMapCHS.json')
    p.add_argument('--output', type=Path, default=root / 'data/story/character/hutao.json')
    a = p.parse_args()
    text_map = json.loads(a.source.read_text(encoding='utf-8'))
    records = []
    for key, title in STORIES.items():
        raw = text_map.get(key, '')
        if not raw or '胡桃' not in raw:
            raise ValueError(f'Missing or unexpected source: {key}')
        records.append(dict(text_hash=key, title=title, raw_text=raw,
                            paragraphs=raw.replace('\\n', '\n').splitlines()))
    payload = dict(version=1, character='胡桃', source=dict(
        repository='https://github.com/Sycamore0/GenshinData',
        revision='7ad6457973f718484ef8b36569b5f76fab628084',
        file='TextMap/TextMapCHS.json', sha256=hashlib.sha256(a.source.read_bytes()).hexdigest()),
        stories=records)
    a.output.parent.mkdir(parents=True, exist_ok=True)
    a.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'{len(records)} complete source strings, {sum(len(r["paragraphs"]) for r in records)} paragraphs -> {a.output}')

if __name__ == '__main__':
    main()
