// 把 Hu_Tao.zip（genshin-voice 归档）整理成规范数据集：
//   data/voice/hutao/wav/{hash}.wav
//   data/voice/hutao/manifest.jsonl   （含文本标签，GPT-SoVITS 可直接用）
import { readdirSync, readFileSync, writeFileSync, copyFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const srcDir = process.argv[2];      // Hu_Tao_extracted（含 .wav + .json）
const dstDir = process.argv[3] || 'data/voice/hutao';

const wavDir = join(dstDir, 'wav');
mkdirSync(wavDir, { recursive: true });

function wavDurationMs(buf) {
  try {
    const rate = buf.readUInt32LE(24);
    const byteRate = buf.readUInt32LE(28);
    const dataSize = buf.readUInt32LE(40);
    if (!byteRate) return 0;
    return Math.round((dataSize / byteRate) * 1000);
  } catch {
    return 0;
  }
}

const jsons = readdirSync(srcDir).filter((f) => f.endsWith('.json')).sort();
const lines = [];
let totalMs = 0;

for (const jf of jsons) {
  const hash = jf.replace('.json', '');
  const meta = JSON.parse(readFileSync(join(srcDir, jf), 'utf8'));
  const srcWav = join(srcDir, `${hash}.wav`);
  const dstWav = join(wavDir, `${hash}.wav`);
  copyFileSync(srcWav, dstWav);

  const wavBuf = readFileSync(dstWav);
  const durMs = wavDurationMs(wavBuf);
  totalMs += durMs;

  const stem = (meta.inGameFilename || '').split(/[\\/]/).pop() || '';
  lines.push(JSON.stringify({
    id: hash,
    audio: `wav/${hash}.wav`,
    text: meta.transcription || '',
    speaker: meta.speaker || 'Hu Tao',
    lang: 'zh',
    source_file: stem,
    duration_ms: durMs,
  }));
}

writeFileSync(join(dstDir, 'manifest.jsonl'), lines.join('\n') + '\n', 'utf8');
console.log(`[done] ${lines.length} 条 -> ${join(dstDir, 'manifest.jsonl')}`);
console.log(`总时长: ${(totalMs / 60000).toFixed(1)} 分钟`);
