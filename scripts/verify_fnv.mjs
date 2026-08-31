// 验证：wem hash = FNV1-64(音频源路径)
const target = '00d3ae0ba7689f84'; // 胡桃 vo_HTLQ001_11_hutao_05.wem 的 hash

function fnv1_64(str) {
  let h = 0xcbf29ce484222325n;
  const p = 0x100000001b3n, m = 0xffffffffffffffffn;
  for (const c of Buffer.from(str, 'utf8')) { h = (h * p) & m; h ^= BigInt(c); }
  return h.toString(16).padStart(16, '0');
}
function fnv1a_64(str) {
  let h = 0xcbf29ce484222325n;
  const p = 0x100000001b3n, m = 0xffffffffffffffffn;
  for (const c of Buffer.from(str, 'utf8')) { h ^= BigInt(c); h = (h * p) & m; }
  return h.toString(16).padStart(16, '0');
}

const paths = [
  'vo_HTLQ001_11_hutao_05.wem',
  'VO_LQ\\VO_hutao\\vo_HTLQ001_11_hutao_05.wem',
  'Chinese\\VO_LQ\\VO_hutao\\vo_HTLQ001_11_hutao_05.wem',
  'VO_hutao\\vo_HTLQ001_11_hutao_05.wem',
  'Chinese\\VO_hutao\\vo_HTLQ001_11_hutao_05.wem',
];

console.log('target:', target);
for (const p of paths) {
  console.log(`\n"${p}"`);
  console.log('  FNV1  :', fnv1_64(p));
  console.log('  FNV1a :', fnv1a_64(p));
}
