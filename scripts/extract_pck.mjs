// 原神 Wwise .pck 解包器：提取内部 .wem 音频
// 结构（已验证）：0x00 "AKPK"；0x38 u32 文件数 N；0x3c 起 N 个 24 字节条目
//   每条 = {u64 hash, u32 flag, u32 size, u32 offset, u32 reserved}
// 用法：node extract_pck.mjs <file.pck> <outDir> [--list]
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, basename } from 'node:path';

const pckPath = process.argv[2];
const outDir = process.argv[3];
const listOnly = process.argv.includes('--list');

if (!pckPath) {
  console.error('usage: node extract_pck.mjs <file.pck> <outDir> [--list]');
  process.exit(1);
}

const buf = readFileSync(pckPath);
if (buf.toString('latin1', 0, 4) !== 'AKPK') {
  console.error('not an AKPK pck file');
  process.exit(1);
}

const count = buf.readUInt32LE(0x38);
const entries = [];
let off = 0x3c;
for (let i = 0; i < count; i++) {
  const hashLo = buf.readUInt32LE(off);
  const hashHi = buf.readUInt32LE(off + 4);
  const flag = buf.readUInt32LE(off + 8);
  const size = buf.readUInt32LE(off + 12);
  const offset = buf.readUInt32LE(off + 16);
  entries.push({ hashHi, hashLo, flag, size, offset });
  off += 24;
}

console.log(`[${basename(pckPath)}] ${count} entries, data region begins @0x${off.toString(16)}`);

if (listOnly) {
  for (const e of entries) {
    const id = e.hashHi.toString(16).padStart(8, '0') + e.hashLo.toString(16).padStart(8, '0');
    console.log(`  ${id}  off=0x${e.offset.toString(16)}  size=${e.size}`);
  }
  process.exit(0);
}

mkdirSync(outDir, { recursive: true });
let n = 0;
for (const e of entries) {
  if (e.offset + e.size > buf.length) {
    console.error(`  skip out-of-range entry at 0x${e.offset.toString(16)} size ${e.size}`);
    continue;
  }
  const id = e.hashHi.toString(16).padStart(8, '0') + e.hashLo.toString(16).padStart(8, '0');
  const data = buf.subarray(e.offset, e.offset + e.size);
  writeFileSync(join(outDir, `${id}.wem`), data);
  n++;
}
console.log(`extracted ${n}/${count} .wem -> ${outDir}`);
