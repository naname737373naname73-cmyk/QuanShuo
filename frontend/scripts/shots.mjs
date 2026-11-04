// 基线截图：npm run shots（全部）或 npm run shots -- --preview（一张）。
// 优先用真实显卡；没有显卡时设 QS_SWIFTSHADER=1。
// 新场景细节极多，全部用 JPEG，并且只在需要时铺满尺寸和时段，否则仓库会膨胀到几百 MB。
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { mkdir, readdir, rm, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const output = resolve(frontend, '../docs/frontend/screenshots');
const motionOutput = resolve(frontend, '../docs/frontend/motion');
const preview = process.argv.includes('--preview');
await mkdir(output, { recursive: true });
await mkdir(motionOutput, { recursive: true });
if (!preview) {
  for (const directory of [output, motionOutput]) {
    for (const file of await readdir(directory)) await rm(resolve(directory, file));
  }
}
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5174, strictPort: true }, logLevel: 'error' });
await server.listen();
const chromeCandidates = [
  process.env.CHROME_PATH,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
].filter(Boolean);
const executablePath = chromeCandidates.find((candidate) => existsSync(candidate));
const browser = await chromium.launch({
  headless: true,
  executablePath,
  args: process.env.QS_SWIFTSHADER
    ? ['--use-angle=swiftshader', '--enable-unsafe-swiftshader']
    : ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist'],
});
const base = 'http://127.0.0.1:5174/';
const small = { width: 1366, height: 768 }, medium = { width: 1600, height: 1000 }, large = { width: 2560, height: 1440 };
const periods = ['dawn', 'noon', 'dusk', 'night'];
const everyPeriod = [0, 1, 2, 3], twoPeriods = [1, 3];
// [文件名, state, 额外参数, 尺寸, 时段]
const plan = [
  ['normal', 'normal', 'records=23', [small, medium, large], everyPeriod],
  ['detail', 'detail', '', [small, medium, large], everyPeriod],
  ['selected', 'selected', '', [medium], twoPeriods],
  ['hover', 'hover', '', [medium], twoPeriods],
  ['recording', 'recording', 'wind=0.8', [medium], [2, 3]],
  ['failed', 'failed', '', [medium], twoPeriods],
  ['empty', 'empty', '', [medium], twoPeriods],
  ['loading', 'loading', '', [medium], twoPeriods],
  ['search-empty', 'search-empty', '', [medium], twoPeriods],
  ['settings', 'settings', '', [medium], twoPeriods],
  ['onboarding', 'onboarding', '', [medium], twoPeriods],
  ['quiet', 'normal', 'quiet=1', [medium], [3]],
  ['fallback', 'normal', 'webgl=0', [medium], [3]],
  // 海棠不随时段变化，只拍一遍
  ['crabapple-normal', 'normal', 'skin=crabapple&records=8', [small, medium], [1]],
  ['crabapple-detail', 'detail', 'skin=crabapple', [medium], [1]],
  ['crabapple-empty', 'empty', 'skin=crabapple', [medium], [1]],
  ['crabapple-full', 'normal', 'skin=crabapple&records=16', [medium], [1]],
  ['crabapple-settings', 'settings', 'skin=crabapple', [medium], [1]],
  ['crabapple-onboarding', 'onboarding', 'skin=crabapple', [medium], [1]],
];
const errors = [];
let count = 0;

async function open(url, size, label) {
  const page = await browser.newPage({ viewport: size, deviceScaleFactor: 1 });
  page.on('pageerror', (error) => errors.push(`${label}: ${error.message}`));
  page.on('response', (response) => {
    if (response.url().startsWith(base) && response.status() >= 400 && !response.url().endsWith('favicon.ico')) errors.push(`${label}: HTTP ${response.status()} ${response.url()}`);
  });
  await page.goto(url, { waitUntil: 'networkidle' });
  await page.waitForFunction(() => document.body.dataset.appReady === 'true' && document.body.dataset.sceneReady === 'true');
  await page.waitForTimeout(500);
  return page;
}

try {
  const items = preview ? [['preview', 'normal', 'records=23', [medium], [3]]] : plan;
  for (const [name, state, extra, sizes, days] of items) {
    for (const size of sizes) {
      for (const day of days) {
        const query = `state=${state}&day=${day}&records=8&t=12.5&seed=1${extra ? `&${extra}` : ''}`;
        const page = await open(`${base}?${query}`, size, name);
        await page.screenshot({ path: resolve(output, `${name}-${size.width}x${size.height}-${periods[day]}.jpg`), type: 'jpeg', quality: 86 });
        await page.close();
        count += 1;
      }
    }
  }

  if (!preview) {
    // 动效序列：单张看不出来的东西，用固定时钟逐帧截
    for (const [index, time] of [0, 0.8, 1.6, 2.4, 3.2, 4].entries()) {
      const page = await open(`${base}?state=recording&day=2&records=8&t=${20 + time}&seed=1&wind=0.85`, medium, 'voice');
      await page.screenshot({ path: resolve(motionOutput, `voice-${String(index).padStart(2, '0')}.jpg`), type: 'jpeg', quality: 80 });
      await page.close();
    }
    const page = await open(`${base}?state=normal&day=3&records=8&seed=1&debug=1`, medium, 'metrics');
    await page.waitForTimeout(3000);
    const metrics = await page.evaluate(() => {
      const gl = document.querySelector('.lake').getContext('webgl2');
      const pixel = new Uint8Array(4);
      const started = performance.now();
      // gl.finish 在 Chrome 里不等 GPU，只有 readPixels 才算数
      for (let index = 0; index < 12; index++) { gl.drawArrays(gl.TRIANGLES, 0, 3); gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel); }
      const extension = gl.getExtension('WEBGL_debug_renderer_info');
      return {
        measuredAt: new Date().toISOString(),
        viewport: '1600x1000',
        gpuMsPerFrame: Number(((performance.now() - started) / 12).toFixed(2)),
        debugText: document.querySelector('.debug-metrics')?.textContent,
        renderer: extension ? gl.getParameter(extension.UNMASKED_RENDERER_WEBGL) : 'unknown',
        usedJsHeapBytes: performance.memory?.usedJSHeapSize ?? null,
        note: '无头 Chrome 的前端测量，不等于原生窗口里的整机占用。',
      };
    });
    await writeFile(resolve(frontend, '../docs/frontend/baseline-metrics.json'), `${JSON.stringify(metrics, null, 2)}\n`);
    await page.close();
  }
  if (errors.length) throw new Error(errors.join('\n'));
  console.log(preview ? '预览截图完成。' : `完成 ${count} 张基线截图和 6 帧涟漪序列。`);
} finally {
  await browser.close();
  await server.close();
}
