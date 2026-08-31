// 从胡桃 manifest 挑「重要、短促、朗朗上口」的语音，生成 quotes.json（带标签+文字，供一字不差复刻）
import { readFileSync, writeFileSync } from 'node:fs';

const manifest = process.argv[2] || 'E:/tomorrow/AIGC/hutao-companion/data/voice/hutao/manifest.jsonl';
const out = process.argv[3] || 'E:/tomorrow/AIGC/hutao-companion/data/persona/quotes.json';

const lines = readFileSync(manifest, 'utf8').trim().split('\n').map((l) => JSON.parse(l));

function category(f) {
  if (!f) return '其他';
  if (f.includes('battle')) return '战斗';
  if (f.includes('mimitomo') || f.includes('friendship')) return '好感度';
  if (f.includes('weather')) return '天气';
  if (f.includes('dialog')) return '对话';
  if (f.includes('explore')) return '探索';
  if (f.includes('spice')) return '料理';
  if (f.includes('card')) return '卡牌';
  if (/vo_(HTLQ|BZLQ|ZBLQ|EQHDJ)/.test(f)) return '剧情';
  return '其他';
}

// 标志性内容：语气词 / 口头禅 / 招牌词（可用第 4 个参数传入自定义正则）
const marker = process.argv[4]
  ? new RegExp(process.argv[4])
  : /[！？!?～~呀啦咯嘛喽哦喔欸嘿哼哈]|本堂主|客卿|生死|往生堂|丘丘|客户|生意|优惠/;

const quotes = lines
  .filter((x) => x.text && x.text.length >= 3 && x.text.length <= 30)
  .filter((x) => x.duration_ms >= 1500 && x.duration_ms <= 6000)
  .filter((x) => marker.test(x.text))
  .map((x) => ({
    text: x.text,
    source_file: x.source_file,
    category: category(x.source_file),
    duration_ms: x.duration_ms,
  }));

// 按文本去重
const seen = new Set();
const uniq = quotes.filter((q) => (seen.has(q.text) ? false : (seen.add(q.text), true)));

writeFileSync(out, JSON.stringify({ signature_voices: uniq }, null, 2), 'utf8');
console.log(`挑出 ${uniq.length} 条 -> ${out}`);
console.log('\n=== 预览（前 40）===');
for (const q of uniq.slice(0, 40)) {
  console.log(`[${q.category}] ${(q.duration_ms / 1000).toFixed(1)}s  ${q.text}`);
}
