// Runtime smoke test for the generated demo page.
//
// Headless Chrome is unavailable in this environment (exit 13, "Multiple targets are not
// supported in headless mode", even for a trivial page), so the page's script is executed
// against a minimal DOM stub instead. That still catches the failure mode that matters:
// a render function throwing on real data (missing field, bad selector, empty list).
import fs from 'node:fs';

const htmlPath = process.argv[2];
const html = fs.readFileSync(htmlPath, 'utf8');
const match = html.match(/<script>([\s\S]*)<\/script>/);
if (!match) throw new Error('no <script> block found');

class FakeEl {
  constructor(sel = '') {
    this.sel = sel; this.innerHTML = ''; this.textContent = ''; this.value = '';
    this.className = ''; this.dataset = {}; this.children = []; this.offsetWidth = 1;
    this.classList = {
      _s: new Set(),
      add: (c) => this.classList._s.add(c),
      remove: (c) => this.classList._s.delete(c),
      toggle: (c, on) => (on === undefined ? (this.classList._s.has(c) ? this.classList._s.delete(c) : this.classList._s.add(c)) : (on ? this.classList._s.add(c) : this.classList._s.delete(c))),
      contains: (c) => this.classList._s.has(c),
    };
  }
  appendChild(c) { this.children.push(c); return c; }
  querySelector() { return new FakeEl(); }
  querySelectorAll() { return []; }
  closest() { return null; }
  scrollIntoView() {}
  before() {}
  addEventListener() {}
}

const registry = new Map();
const el = (sel) => { if (!registry.has(sel)) registry.set(sel, new FakeEl(sel)); return registry.get(sel); };
const document = {
  querySelector: (sel) => el(sel),
  querySelectorAll: () => [],
  createElement: () => new FakeEl(),
  addEventListener: () => {},
};

let failure = null;
let picked = null;
try {
  // Append a hook so the harness can drive a specific case through the real render path.
  // DATA/selected/renderCase all live inside this function scope, so the lookup happens there.
  const body = match[1] + `
    ;globalThis.__pickScopeCase = () => {
       const c = DATA.cases.find(x => (x.steps || []).some(s => s.strategy === 'ChapterScope'));
       if (!c) return null;
       selected = c.id; renderCase(c); return c.id;
     };`;
  new Function('document', 'window', 'setInterval', 'clearInterval', body)(
    document, { addEventListener: () => {} },
    () => 0, () => {});
  picked = globalThis.__pickScopeCase();
} catch (error) {
  failure = error;
}

if (failure) {
  console.error('RUNTIME ERROR: ' + failure.message);
  console.error(failure.stack.split('\n').slice(0, 4).join('\n'));
  process.exit(1);
}

const host = registry.get('#caseHost');
const main = registry.get('#main');
const list = registry.get('#list');
const checks = [
  ['header rendered', /语料/.test(registry.get('#hdr-meta').innerHTML)],
  ['arm legend rendered', /class="item"/.test(main.innerHTML)],
  ['stats rendered', /召回步/.test(main.innerHTML)],
  ['step status panel rendered', /零证据/.test(main.innerHTML)],
  ['case list rendered', list.children.length > 0],
  ['case detail stage 0-6 rendered', ['0', '1', '2', '3', '4', '5', '6'].every(s => host.innerHTML.includes(`data-stage="${s}"`))],
  ['step cards rendered', /class="step"/.test(host.innerHTML)],
  ['evidence rows rendered', /<table>/.test(host.innerHTML)],
  ['agent-chosen arm badge rendered', /agent 选/.test(host.innerHTML)],
  ['chapter-scope case rendered', picked ? /扩章节/.test(host.innerHTML) : true],
];
let bad = 0;
for (const [name, ok] of checks) { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}`); if (!ok) bad++; }
console.log(`cases=${list.children.length} hostChars=${host.innerHTML.length}`);
process.exit(bad === 0 ? 0 : 1);
