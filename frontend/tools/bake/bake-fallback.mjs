// 生成 WebGL 不可用时的静态回退图：node tools/bake/bake-fallback.mjs
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import config from '../../src/config/scene.config.json' with { type: 'json' };

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5184, strictPort: true }, logLevel: 'error' });
await server.listen();
const candidates = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'];
const browser = await chromium.launch({
  headless: true,
  executablePath: candidates.find((path) => existsSync(path)),
  args: ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist'],
});
try {
  for (const [day, period] of config.periods.entries()) {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1200 } });
    await page.goto(`http://127.0.0.1:5184/tools/bake/preview.html?day=${day}&t=12.5`);
    await page.waitForFunction(() => window.lakeReady === true);
    await page.waitForTimeout(300);
    await page.screenshot({ path: resolve(frontend, `public/assets/lake/fallback-${period.id}.jpg`), type: 'jpeg', quality: 82 });
    await page.close();
    console.log('ok', period.id);
  }
} finally {
  await browser.close();
  await server.close();
}
