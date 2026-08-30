// 用 node 全局 fetch 下载，支持长超时与重试（绕过 curl 的 schannel 凭据问题）
import { writeFileSync, mkdirSync } from 'node:fs';

const [url, outPath] = process.argv.slice(2);
if (!url || !outPath) {
  console.error('usage: node download_tool.mjs <url> <outPath>');
  process.exit(1);
}

async function fetchOnce() {
  const res = await fetch(url, { redirect: 'follow', signal: AbortSignal.timeout(120000) });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return Buffer.from(await res.arrayBuffer());
}

let lastErr;
for (let attempt = 1; attempt <= 4; attempt++) {
  try {
    const buf = await fetchOnce();
    mkdirSync(outPath.replace(/[/\\][^/\\]+$/, ''), { recursive: true });
    writeFileSync(outPath, buf);
    console.log(`[done] ${buf.length} bytes -> ${outPath}`);
    process.exit(0);
  } catch (e) {
    lastErr = e;
    console.error(`[retry ${attempt}/4] ${e.message}`);
    await new Promise((r) => setTimeout(r, 3000));
  }
}
console.error(`[error] failed: ${lastErr}`);
process.exit(1);
