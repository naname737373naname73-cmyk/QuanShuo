// 星湖语音框的玻璃底图：node tools/bake-voice-glass.mjs
// 在真实的湖景上放一块和星图日历同样参数的玻璃，再加一点地平线的桃色，截下来给原生窗口用。
// 产出 src/QuanShuo.Native/Assets/Starlake/night.png 和 dusk.png。
// 只有玻璃本身。边框、星尘、星轨、月亮和文字都是窗口按当前大小画的。
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const output = resolve(frontend, '../src/QuanShuo.Native/Assets/Starlake');
mkdirSync(output, { recursive: true });

const WIDTH = 560;
const HEIGHT = 280;
const PIXEL_RATIO = 2;
// 窗口默认出现在屏幕底部居中，玻璃后面是湖面和倒影
const VIEW = { width: 1426, height: 900 };
const LEFT = (VIEW.width - WIDTH) / 2;
const TOP = VIEW.height - HEIGHT - 64;

for (const key of ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy']) delete process.env[key];
process.env.NO_PROXY = '127.0.0.1,localhost';
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5185, strictPort: true }, logLevel: 'error' });
await server.listen();
const executablePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
const browser = await chromium.launch({ headless: true, executablePath, args: ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist', '--no-proxy-server'] });

try {
  for (const [name, day] of [['night', 3], ['dusk', 2]]) {
    const page = await browser.newPage({ viewport: VIEW, deviceScaleFactor: PIXEL_RATIO });
    await page.goto(`http://127.0.0.1:5185/?state=normal&day=${day}&records=8&t=12.5&seed=1`, { waitUntil: 'networkidle' });
    await page.waitForFunction(() => document.body.dataset.appReady === 'true' && document.body.dataset.sceneReady === 'true', null, { timeout: 30000 });
    await page.evaluate(() => { document.querySelector('.app').dataset.journal = 'closed'; });
    await page.waitForTimeout(1500);
    await page.evaluate(({ left, top, width, height }) => {
      const glass = document.createElement('div');
      glass.style.cssText = `position:absolute;z-index:41;left:${left}px;top:${top}px;width:${width}px;height:${height}px;overflow:hidden;`
        + 'background:linear-gradient(170deg,rgba(14,20,56,.50),rgba(7,11,32,.66));backdrop-filter:blur(20px) saturate(1.45);';
      const glow = document.createElement('div');
      glow.style.cssText = `position:absolute;left:${width * -.12}px;right:${width * -.12}px;bottom:${height * -.34}px;height:${height * .68}px;`
        + 'background:radial-gradient(closest-side,rgba(255,201,163,.13),rgba(255,201,163,0));';
      glass.append(glow);
      document.body.append(glass);
    }, { left: LEFT, top: TOP, width: WIDTH, height: HEIGHT });
    await page.waitForTimeout(400);
    await page.screenshot({ path: resolve(output, `${name}.png`), clip: { x: LEFT, y: TOP, width: WIDTH, height: HEIGHT } });
    console.log(name, 'ok');
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
process.exit(0);
