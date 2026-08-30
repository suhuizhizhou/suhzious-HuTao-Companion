// 批量解包原神角色语音 pck，按角色分类，生成 voice_manifest.json
// 用法：
//   node extract_all.mjs <pckDir> <outRoot>            # 解包目录下所有 10*.pck
//   node extract_all.mjs <single.pck> <outRoot>        # 解包单个 pck
//   node extract_all.mjs <pckDir> <outRoot> --decode   # 额外用 vgmstream 解码 wav
import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join, basename } from 'node:path';
import { execFileSync } from 'node:child_process';

// 角色名映射（avatar id -> 中文名），基于权威 AvatarExcelConfigData 的 ID 顺序 + 星级校准
const NAMES = {
  10000001: '旅行者', 10000002: '旅行者', 10000005: '旅行者·空', 10000007: '旅行者·荧',
  10000003: '琴', 10000006: '丽莎', 10000014: '芭芭拉', 10000015: '凯亚', 10000016: '迪卢克',
  10000020: '雷泽', 10000021: '安柏', 10000022: '温迪', 10000023: '香菱', 10000024: '北斗',
  10000025: '行秋', 10000026: '魈', 10000027: '凝光', 10000029: '可莉', 10000030: '钟离',
  10000031: '菲谢尔', 10000032: '班尼特', 10000033: '达达利亚', 10000034: '诺艾尔', 10000035: '七七',
  10000036: '重云', 10000037: '甘雨', 10000038: '阿贝多', 10000039: '迪奥娜', 10000041: '莫娜',
  10000042: '刻晴', 10000043: '砂糖', 10000044: '辛焱', 10000045: '罗莎莉亚', 10000046: '胡桃',
  10000047: '枫原万叶', 10000048: '烟绯', 10000051: '优菈',
};

// pck 编号 -> avatar id：pck = 1000 + (avatarId % 100) - 1  =>  avatarId 后两位 = pck - 999
function pckToAvatar(pck) {
  const last2 = pck - 999;
  if (last2 < 0 || last2 > 200) return null;
  // avatar id 段：10000000 + last2（但需注意 10000004/08-13/17-19/28/40 等不存在）
  const id = 10000000 + last2;
  return id;
}
function avatarName(id) {
  return NAMES[id] || `未知-${id}`;
}

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
    entries.push({ hashHi, hashLo, size, offset });
    off += 24;
  }
  return entries;
}

// 从 .wem 头解析时长（秒）
function wemDuration(data) {
  try {
    if (data.toString('latin1', 0, 4) !== 'RIFF') return 0;
    const rate = data.readUInt32LE(0x18);
    const samples = data.readUInt32LE(0x2c);
    if (!rate) return 0;
    return samples / rate;
  } catch {
    return 0;
  }
}

function hashHex(e) {
  return e.hashHi.toString(16).padStart(8, '0') + e.hashLo.toString(16).padStart(8, '0');
}

const [input, outRoot] = process.argv.slice(2);
const decode = process.argv.includes('--decode');
const vgm = 'E:/tomorrow/AIGC/hutao-companion/tools/vgmstream-cli.exe';

if (!input || !outRoot) {
  console.error('usage: node extract_all.mjs <pckDir|pck> <outRoot> [--decode]');
  process.exit(1);
}

let pckFiles = [];
if (existsSync(input) && statSync(input).isFile()) {
  pckFiles = [input];
} else {
  pckFiles = readdirSync(input).filter((f) => /^10\d+\.pck$/.test(f)).map((f) => join(input, f)).sort();
}

console.log(`[extract_all] ${pckFiles.length} pck files`);
const manifest = { generated_by: 'extract_all.mjs', mapping_note: 'pck = 1000 + (avatarId%100) - 1, 待校准', characters: {} };

for (const pck of pckFiles) {
  const pckId = parseInt(basename(pck).replace(/\.pck$/, ''), 10);
  const avatarId = pckToAvatar(pckId);
  const name = avatarId ? avatarName(avatarId) : `未知-pck${pckId}`;

  const buf = readFileSync(pck);
  const entries = parsePck(buf);
  if (!entries) {
    console.error(`[skip] ${basename(pck)} 非 AKPK`);
    continue;
  }

  const outDir = join(outRoot, String(pckId));
  mkdirSync(outDir, { recursive: true });

  const files = [];
  let totalDur = 0;
  for (const e of entries) {
    if (e.offset + e.size > buf.length) continue;
    const data = buf.subarray(e.offset, e.offset + e.size);
    const id = hashHex(e);
    const dur = wemDuration(data);
    totalDur += dur;
    writeFileSync(join(outDir, `${id}.wem`), data);
    files.push({ wem: `${id}.wem`, wem_hash: id, size: e.size, duration_sec: +dur.toFixed(3) });
  }

  manifest.characters[String(pckId)] = {
    character: name,
    avatar_id: avatarId,
    pck: basename(pck),
    pck_size_bytes: buf.length,
    file_count: files.length,
    total_duration_sec: +totalDur.toFixed(1),
    files,
  };

  console.log(`[${pckId}] ${name} (avatar ${avatarId}) -> ${files.length} 条, 共 ${totalDur.toFixed(0)}s`);

  if (decode && existsSync(vgm)) {
    mkdirSync(join(outRoot, String(pckId) + '_wav'), { recursive: true });
    for (const f of files) {
      const src = join(outDir, f.wem);
      const dst = join(outRoot, String(pckId) + '_wav', f.wem.replace('.wem', '.wav'));
      try { execFileSync(vgm, ['-o', dst, src], { stdio: 'ignore' }); } catch {}
    }
    console.log(`  -> decoded wav`);
  }
}

writeFileSync(join(outRoot, 'voice_manifest.json'), JSON.stringify(manifest, null, 2), 'utf8');
console.log(`[done] manifest -> ${join(outRoot, 'voice_manifest.json')}`);
