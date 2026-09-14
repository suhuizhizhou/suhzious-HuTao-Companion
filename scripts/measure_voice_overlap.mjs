// 量化「RAG 语料 ↔ 角色原声清单」的重合度，用来判断原声优先功能到底能不能触发。
//
// 两个回答：
//   1) 剧情语料里有多少台词能逐字对上角色本人录过的原声（决定 RAG 逐字直连的覆盖率）
//   2) 有多少原声在语料里查无此句（决定只靠剧情检索能捞回多少）
//
// 用法：node scripts/measure_voice_overlap.mjs [hutao] [furina]
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const characters = process.argv.slice(2);
if (characters.length === 0) characters.push('hutao', 'furina');

const normalize = (s) => (s ?? '')
  .replace(/[\s，。！？、；：…—~～,.!?;:'‘’“”"「」『』（）()《》〈〉·]/g, '')
  .replace(/<[^>]*>/g, '')
  .toLowerCase();

function loadManifest(character) {
  const file = join(root, 'data', 'voice', character, 'manifest.jsonl');
  const byText = new Map();
  if (!existsSync(file)) return byText;
  for (const line of readFileSync(file, 'utf8').split('\n')) {
    if (!line.trim()) continue;
    let row;
    try { row = JSON.parse(line); } catch { continue; }
    if (!row.text || !row.audio) continue;
    const key = normalize(row.text);
    if (key.length < 2) continue;
    if (!existsSync(join(root, 'data', 'voice', character, row.audio))) continue;
    if (!byText.has(key)) byText.set(key, []);
    byText.get(key).push({ id: row.id, text: row.text.trim(), durationMs: row.duration_ms ?? 0 });
  }
  return byText;
}

function loadStoryLines() {
  const dir = join(root, 'data', 'story', 'dialogue', 'chapters');
  if (!existsSync(dir)) return [];
  const rows = [];
  for (const file of readdirSync(dir)) {
    if (!file.endsWith('.jsonl')) continue;
    for (const line of readFileSync(join(dir, file), 'utf8').split('\n')) {
      if (!line.trim()) continue;
      let row;
      try { row = JSON.parse(line); } catch { continue; }
      if (!row.text) continue;
      rows.push({
        speaker: row.speaker ?? '',
        text: row.text,
        scene: `${file}:${row.subquest_index ?? 0}:${row.talk_id ?? 0}`,
      });
    }
  }
  return rows;
}

const storyLines = loadStoryLines();
const sceneCount = new Set(storyLines.map((r) => r.scene)).size;
console.log(`剧情语料台词 ${storyLines.length}，场景 ${sceneCount}`);

const speakerOf = { hutao: ['胡桃'], furina: ['芙宁娜'], klee: ['可莉'] };

for (const character of characters) {
  const byText = loadManifest(character);
  if (byText.size === 0) {
    console.log(`\n[${character}] 未找到 data/voice/${character}/manifest.jsonl，跳过`);
    continue;
  }

  const clips = [...byText.values()].flat();
  const names = new Set(speakerOf[character] ?? []);
  const linkedScenes = new Set();
  const linkedClips = new Set();
  let selfLines = 0;
  let anyLines = 0;

  for (const row of storyLines) {
    const hits = byText.get(normalize(row.text));
    if (!hits) continue;
    anyLines++;
    if (!names.has(row.speaker)) continue; // 只认本角色自己说的台词
    selfLines++;
    linkedScenes.add(row.scene);
    for (const hit of hits) linkedClips.add(hit.id);
  }

  const corpusTexts = new Set(storyLines.map((r) => normalize(r.text)));
  const orphans = clips.filter((c) => !corpusTexts.has(normalize(c.text)));

  console.log(`\n[${character}] 原声片段 ${clips.length}（唯一文本 ${byText.size}）`);
  console.log(`  逐字命中剧情台词（不限说话人）: ${anyLines}`);
  console.log(`  其中说话人确实是本角色: ${selfLines}   ← 别人说同样的话属于假匹配`);
  console.log(`  覆盖场景数: ${linkedScenes.size} / ${sceneCount}`);
  console.log(`  可被 RAG 逐字直连的原声: ${linkedClips.size}`);
  console.log(`  孤儿片段（语料里查无此句）: ${orphans.length} (${(orphans.length / clips.length * 100).toFixed(1)}%)`);
  console.log(`  孤儿样例: ${orphans.slice(0, 3).map((o) => o.text.slice(0, 16)).join(' | ')}`);
}
