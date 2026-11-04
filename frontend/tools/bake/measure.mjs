// 用真实显卡量帧耗时：node tools/bake/measure.mjs
// 输出每种窗口尺寸下一帧的 GPU 耗时。rAF 在无头模式下不受垂直同步限制，所以不看 rAF 次数。
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5183, strictPort: true }, logLevel: 'error' });
await server.listen();
const candidates = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'];
const browser = await chromium.launch({
  headless: true,
  executablePath: candidates.find((path) => existsSync(path)),
  args: ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist', '--disable-frame-rate-limit', '--disable-gpu-vsync'],
});
try {
  for (const [width, height] of [[1366, 768], [1600, 1000], [2560, 1440], [3840, 2160]]) {
    const page = await browser.newPage({ viewport: { width, height } });
    await page.goto('http://127.0.0.1:5183/?state=recording&day=3&records=23&wind=0.8&debug=1', { waitUntil: 'load' });
    await page.waitForFunction(() => document.body.dataset.appReady === 'true');
    await page.waitForTimeout(1000);
    const result = await page.evaluate(() => new Promise((done) => {
      const canvas = document.querySelector('.lake');
      const gl = canvas.getContext('webgl2');
      const stamps = [];
      const tick = (time) => {
        stamps.push(time);
        if (stamps.length < 150) requestAnimationFrame(tick);
        else {
          const started = performance.now();
          const pixel = new Uint8Array(4);
          // gl.finish 在 Chrome 里不等 GPU；readPixels 才会强制同步，否则测出来是 0ms
          for (let i = 0; i < 12; i++) { gl.drawArrays(gl.TRIANGLES, 0, 3); gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel); }
          const pureGpu = (performance.now() - started) / 12;
          done({ canvas: `${canvas.width}x${canvas.height}`, fps: Math.round((stamps.length - 1) / total * 1000), gpuMsPerFrame: Number(pureGpu.toFixed(2)), panel: document.querySelector('.debug-metrics')?.textContent ?? '' });
        }
      };
      requestAnimationFrame(tick);
    }));
    console.log(`${width}x${height}`, JSON.stringify(result));
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
