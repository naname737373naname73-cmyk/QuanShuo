// 用本机显卡离线烘焙远山光照数据：node tools/bake/bake-terrain.mjs [--w 2880 --h 720 --aa 3]
// 需要真实 GPU；在 SwiftShader 上也能跑，只是很慢。
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const frontend = resolve(here, '../..');
const arg = (name, fallback) => { const i = process.argv.indexOf(`--${name}`); return i >= 0 ? process.argv[i + 1] : fallback; };
const w = arg('w', '2880'), h = arg('h', '720'), aa = arg('aa', '3');
const out = resolve(arg('out', resolve(frontend, 'public/assets/lake')));
await mkdir(out, { recursive: true });

const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5181, strictPort: true }, logLevel: 'error' });
await server.listen();
const candidates = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'];
const browser = await chromium.launch({
  headless: true,
  executablePath: candidates.find((path) => existsSync(path)),
  args: ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist', '--disable-gpu-watchdog', '--enable-unsafe-webgpu'],
});
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  page.on('pageerror', (error) => console.error('页面错误', error.message));
  page.on('console', (message) => { if (message.type() === 'error') console.error(message.text()); });
  await page.goto(`http://127.0.0.1:5181/tools/bake/terrain.html?w=${w}&h=${h}&aa=${aa}`);
  await page.waitForFunction(() => window.bakeReady === true, null, { timeout: 120000 });
  console.log('渲染器：', await page.title());
  page.setDefaultTimeout(0);
  for (const [pass, name] of [[0, 'terrain-a.png'], [1, 'terrain-b.png']]) {
    const started = Date.now();
    const dataUrl = await page.evaluate((value) => window.bake(value), pass);
    await writeFile(resolve(out, name), Buffer.from(dataUrl.split(',')[1], 'base64'));
    console.log(name, `${((Date.now() - started) / 1000).toFixed(1)}s`);
  }
} finally {
  await browser.close();
  await server.close();
}
