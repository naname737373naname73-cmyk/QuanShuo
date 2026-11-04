// 用真实显卡截场景预览：node tools/bake/snap.mjs <输出目录> [路径和查询...]
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const [out, ...targets] = process.argv.slice(2);
await mkdir(out, { recursive: true });
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5182, strictPort: true }, logLevel: 'error' });
await server.listen();
const candidates = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'];
const browser = await chromium.launch({
  headless: true,
  executablePath: candidates.find((path) => existsSync(path)),
  args: ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist'],
});
try {
  for (const target of targets) {
    // 目标格式：名字=路径?查询@宽x高
    const [name, rest] = target.split('=', 2).length === 2 ? [target.slice(0, target.indexOf('=')), target.slice(target.indexOf('=') + 1)] : ['shot', target];
    const [path, size = '1600x1000'] = rest.split('@');
    const [width, height] = size.split('x').map(Number);
    const page = await browser.newPage({ viewport: { width, height } });
    page.on('pageerror', (error) => console.error(name, '页面错误', error.message));
    page.on('console', (message) => { if (message.type() === 'error' && !message.text().includes('404')) console.error(name, message.text()); });
    await page.goto(`http://127.0.0.1:5182${path}`, { waitUntil: 'load' });
    await page.waitForFunction(() => window.lakeReady === true || document.body.dataset.appReady === 'true', null, { timeout: 30000 });
    await page.waitForTimeout(350);
    await page.screenshot({ path: resolve(out, `${name}.png`) });
    await page.close();
    console.log('ok', name);
  }
} finally {
  await browser.close();
  await server.close();
}
