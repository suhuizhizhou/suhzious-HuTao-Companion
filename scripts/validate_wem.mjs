// 校验解包出的 .wem 文件完整性：RIFF 头声明的大小 vs 实际文件大小
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2];
if (!dir) {
  console.error('usage: node validate_wem.mjs <dir with .wem files>');
  process.exit(1);
}

const files = readdirSync(dir).filter((f) => f.endsWith('.wem'));
let bad = 0;
let checked = 0;

for (const f of files.slice(0, 200)) {
  const buf = readFileSync(join(dir, f));
  if (buf.length < 12 || buf.toString('latin1', 0, 4) !== 'RIFF') {
    console.log(`[bad-magic] ${f} len=${buf.length}`);
    bad++;
    continue;
  }
  const riffSize = buf.readUInt32LE(4);
  const expected = riffSize + 8;
  checked++;
  if (expected !== buf.length) {
    const diff = buf.length - expected;
    console.log(`[mismatch] ${f}: 实际=${buf.length} RIFF声明=${expected} 差=${diff}字节`);
    bad++;
  }
}
console.log(`\n检查 ${checked} 个，不一致 ${bad} 个`);
