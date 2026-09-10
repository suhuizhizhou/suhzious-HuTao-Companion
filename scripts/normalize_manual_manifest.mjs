// 将 extract_character_manual.mjs 产出的 manifest_manual.jsonl 规范化为
// GPT-SoVITS 与项目数据目录使用的 manifest.jsonl，并补充 WAV 时长。
// 用法：node normalize_manual_manifest.mjs <manifest_manual.jsonl> <data/voice/character>
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const [manualPath, outputDir] = process.argv.slice(2);
if (!manualPath || !outputDir) {
  console.error('usage: node normalize_manual_manifest.mjs <manifest_manual.jsonl> <outputDir>');
  process.exit(1);
}

const root = resolve(outputDir);
mkdirSync(join(root, 'wav'), { recursive: true });

function wavDurationMs(path) {
  try {
    const buf = readFileSync(path);
    const rate = buf.readUInt32LE(24);
    const byteRate = buf.readUInt32LE(28);
    const dataSize = buf.readUInt32LE(40);
    return byteRate > 0 ? Math.round((dataSize / byteRate) * 1000) : 0;
  } catch {
    return 0;
  }
}

const lines = readFileSync(manualPath, 'utf8').split(/\r?\n/).filter(Boolean);
const normalized = [];
for (const line of lines) {
  const item = JSON.parse(line);
  const id = item.id;
  const audio = item.audio || `wav/${id}.wav`;
  const audioPath = resolve(dirname(manualPath), audio);
  const durationMs = wavDurationMs(audioPath);
  normalized.push({
    id,
    audio,
    text: item.text || '',
    speaker: item.speaker || '',
    lang: 'zh',
    source_file: item.source_file || '',
    pck: item.pck || '',
    duration_ms: durationMs,
  });
}

writeFileSync(join(root, 'manifest.jsonl'), normalized.map((item) => JSON.stringify(item)).join('\n') + '\n', 'utf8');
const totalMs = normalized.reduce((sum, item) => sum + item.duration_ms, 0);
console.log(`[done] ${normalized.length} 条 -> ${join(root, 'manifest.jsonl')}`);
console.log(`总时长: ${(totalMs / 60000).toFixed(1)} 分钟`);
