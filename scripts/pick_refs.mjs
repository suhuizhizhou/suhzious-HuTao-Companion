// 从胡桃 manifest 挑选 few-shot 参考音频（3-10 秒，文本完整）
// 输出候选清单，供 GPT-SoVITS 参考音频使用
import { readFileSync, writeFileSync } from 'node:fs';

const manifestPath = process.argv[2] || 'data/voice/hutao/manifest.jsonl';
const outPath = process.argv[3] || 'data/voice/hutao/ref_candidates.json';
const minSec = Number(process.argv[4] || 3);
const maxSec = Number(process.argv[5] || 10);

const lines = readFileSync(manifestPath, 'utf8').trim().split('\n').map((l) => JSON.parse(l));
const cands = lines
  .filter((x) => {
    const s = x.duration_ms / 1000;
    return s >= minSec && s <= maxSec && x.text && x.text.length >= 4 && x.text.length <= 40;
  })
  .map((x) => ({ ...x, duration_sec: +(x.duration_ms / 1000).toFixed(2) }))
  .sort((a, b) => a.duration_ms - b.duration_ms);

console.log(`候选 ${cands.length} 条（${minSec}-${maxSec}s，文本 4-40 字）`);
writeFileSync(outPath, JSON.stringify(cands, null, 2), 'utf8');

// 打印前 15 条（较短、干净），供人工挑选参考
console.log('\n=== 推荐参考候选（前 15）===');
for (const c of cands.slice(0, 15)) {
  console.log(`${c.duration_sec}s  ${c.text}`);
}
console.log(`\n完整清单 -> ${outPath}`);
