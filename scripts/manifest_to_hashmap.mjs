// 从 manifest.jsonl 生成 hash_map.json（供 extract_character_manual.mjs 使用）
import { readFileSync, writeFileSync } from 'node:fs';

const manifest = process.argv[2];
const out = process.argv[3];

const lines = readFileSync(manifest, 'utf8').trim().split('\n').filter(Boolean);
const map = {};
for (const l of lines) {
  const e = JSON.parse(l);
  map[e.id] = { text: e.text || '', speaker: e.speaker || '', file: e.source_file || '' };
}
writeFileSync(out, JSON.stringify(map, null, 2), 'utf8');
console.log(`生成 ${Object.keys(map).length} 条 hash 映射 -> ${out}`);
