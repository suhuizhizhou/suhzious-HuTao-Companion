// 找芙宁娜招牌台词（含水神/剧场/舞台等），用于确定 few-shot 参考音频
import { readFileSync } from 'node:fs';

const cands = JSON.parse(readFileSync('E:/tomorrow/AIGC/hutao-companion/data/voice/furina/ref_candidates.json', 'utf8'));
const hits = cands.filter((r) => /水神|剧场|舞台|演出|谢幕|观众|主角|正义|审判/.test(r.text));
console.log('招牌台词候选:', hits.length, '条');
for (const h of hits.slice(0, 15)) {
  console.log(`${h.id}  ${h.duration_sec}s  ${h.text}`);
}
