// 探查米哈游 .blk / ctable.dat 配置，看是否含语音 hash -> 文件名/文本 映射
import { readFileSync } from 'node:fs';

const file = process.argv[2];
if (!file) { console.error('usage: node inspect_blk.mjs <file>'); process.exit(1); }

const buf = readFileSync(file);
console.log('size:', buf.length);
console.log('头部 64 字节 hex:', buf.subarray(0, 64).toString('hex'));

const s = buf.toString('latin1');
for (const kw of ['VO_hutao', 'VO_furina', 'hutao', 'HuTao', 'Voice', 'Avatar', 'Dialog', 'vo_']) {
  const i = s.indexOf(kw);
  console.log(`${kw}: ${i >= 0 ? 'offset ' + i : '未出现'}`);
}
