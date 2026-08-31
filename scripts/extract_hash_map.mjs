// 从 genshin-voice 解压出的 .json 提取「wem hash -> 台词文本」映射（文本标签来源）
// 用法：node extract_hash_map.mjs <解压目录> <输出 json>
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const srcDir = process.argv[2];
const out = process.argv[3];

const jsons = readdirSync(srcDir).filter((f) => f.endsWith('.json'));
const map = {};
for (const jf of jsons) {
  const hash = jf.replace('.json', '');
  try {
    const meta = JSON.parse(readFileSync(join(srcDir, jf), 'utf8'));
    map[hash] = {
      text: meta.transcription || '',
      speaker: meta.speaker || '',
      file: (meta.inGameFilename || '').split(/[\\/]/).pop() || '',
    };
  } catch {}
}
writeFileSync(out, JSON.stringify(map, null, 2), 'utf8');
console.log(`提取 ${Object.keys(map).length} 条 hash 映射 -> ${out}`);
