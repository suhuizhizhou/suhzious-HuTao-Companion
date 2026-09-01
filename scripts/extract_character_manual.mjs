// 完整手动提取：从游戏 pck 解包 -> hash 匹配角色 -> vgmstream 解码 wav -> 输出 manifest
// 用法：node extract_character_manual.mjs <pckDir> <hashMap.json> <outDir> <vgmstream-cli.exe>
import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync } from 'node:fs';
import { join, basename } from 'node:path';
import { execFileSync } from 'node:child_process';

const [pckDir, hashMapPath, outDir, vgm] = process.argv.slice(2);
if (!pckDir || !hashMapPath || !outDir) {
  console.error('usage: node extract_character_manual.mjs <pckDir> <hashMap.json> <outDir> [vgmstream-cli.exe]');
  process.exit(1);
}

const hashMap = JSON.parse(readFileSync(hashMapPath, 'utf8'));
const targets = new Set(Object.keys(hashMap));
console.log(`目标 hash 数: ${targets.size}`);

function parsePck(buf) {
  if (buf.toString('latin1', 0, 4) !== 'AKPK') return null;
  const count = buf.readUInt32LE(0x38);
  const entries = [];
  let off = 0x3c;
  for (let i = 0; i < count; i++) {
    const hashLo = buf.readUInt32LE(off);
    const hashHi = buf.readUInt32LE(off + 4);
    const size = buf.readUInt32LE(off + 12);
    const offset = buf.readUInt32LE(off + 16);
    entries.push({ id: hashHi.toString(16).padStart(8, '0') + hashLo.toString(16).padStart(8, '0'), size, offset });
    off += 24;
  }
  return entries;
}

// 遍历所有语音 pck（10xx/20xx/30xx/40xx/50xx/External），排除音效/音乐/流式
const pckFiles = readdirSync(pckDir)
  .filter((f) => f.endsWith('.pck') && !/^(Banks|Music|Streamed|Minimum)/.test(f))
  .sort();

const wavDir = join(outDir, 'wav');
mkdirSync(wavDir, { recursive: true });

const manifest = [];
let matched = 0;

for (const f of pckFiles) {
  const buf = readFileSync(join(pckDir, f));
  const entries = parsePck(buf);
  if (!entries) continue;

  let hit = 0;
  for (const e of entries) {
    if (!targets.has(e.id)) continue;
    hit++;
    matched++;
    const meta = hashMap[e.id];
    const wemPath = join(outDir, `_tmp_${e.id}.wem`);
    writeFileSync(wemPath, buf.subarray(e.offset, e.offset + e.size));

    let wavFile = `${e.id}.wav`;
    if (vgm && existsSync(vgm)) {
      try {
        execFileSync(vgm, ['-o', join(wavDir, wavFile), wemPath], { stdio: 'ignore' });
      } catch {
        wavFile = ''; // 解码失败
      }
    } else {
      wavFile = ''; // 无解码器，仅保留文本
    }

    manifest.push({
      id: e.id,
      audio: wavFile ? `wav/${wavFile}` : null,
      text: meta.text,
      speaker: meta.speaker,
      source_file: meta.file,
      pck: f,
    });

    try { execFileSync('cmd', ['/c', 'del', '/q', wemPath]); } catch {}
  }
  console.log(`[${f}] 命中 ${hit} 条（累计 ${matched}）`);
}

writeFileSync(join(outDir, 'manifest_manual.jsonl'), manifest.map((m) => JSON.stringify(m)).join('\n'), 'utf8');
console.log(`\n[done] 匹配 ${matched}/${targets.size} 条，manifest -> ${join(outDir, 'manifest_manual.jsonl')}`);
