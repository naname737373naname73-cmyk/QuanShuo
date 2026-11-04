// 海棠的印花：node tools/bake-crabapple-print.mjs
// 把圆画的淡墨稿 public/assets/crabapple/ink.webp 用开出来的花的颜色淡淡印成一层，产出：
//   public/assets/crabapple/print.webp                 日历四周露出来的印花
//   ../src/QuanShuo.Native/Assets/Crabapple/print.jpg  录音卡片和说话窗口的印花（GDI+ 不一定能读 webp）
// 改了 ink.webp 或者想换浓淡时重跑。颜色和前端 crabapple.css、NativeTheme 的 Print* 是一套。
import { chromium } from 'playwright';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const source = resolve(frontend, 'public/assets/crabapple/ink.webp');
const outputs = {
  webp: resolve(frontend, 'public/assets/crabapple/print.webp'),
  jpeg: resolve(frontend, '../src/QuanShuo.Native/Assets/Crabapple/print.jpg'),
};

// 线稿里比这更亮的是纸和水纹，不印；越暗印得越浓。STRENGTH 是整层的浓度
const PAPER_LIGHTNESS = 0.86, LINE_SPAN = 0.44, CURVE = 0.9, STRENGTH = 0.62;
const GROUND = [245, 238, 226];
const BLOOM = [201, 127, 111];

const chromePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
const browser = await chromium.launch({ headless: true, executablePath: chromePath });
try {
  const page = await browser.newPage();
  const result = await page.evaluate(async ({ webp, settings }) => {
    const image = new Image();
    image.src = `data:image/webp;base64,${webp}`;
    await image.decode();
    const { naturalWidth: width, naturalHeight: height } = image;
    const canvas = new OffscreenCanvas(width, height);
    const context = canvas.getContext('2d', { willReadFrequently: true });
    context.drawImage(image, 0, 0);
    const pixels = context.getImageData(0, 0, width, height);
    const data = pixels.data;
    const { paper, span, curve, strength, ground, bloom } = settings;
    for (let i = 0; i < data.length; i += 4) {
      const lightness = (0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2]) / 255;
      const amount = Math.pow(Math.min(1, Math.max(0, (paper - lightness) / span)), curve) * strength;
      for (let channel = 0; channel < 3; channel++) data[i + channel] = Math.round(ground[channel] + (bloom[channel] - ground[channel]) * amount);
      data[i + 3] = 255;
    }
    context.putImageData(pixels, 0, 0);
    const toBase64 = async (blob) => {
      const bytes = new Uint8Array(await blob.arrayBuffer());
      let text = '';
      for (let start = 0; start < bytes.length; start += 32768) text += String.fromCharCode(...bytes.subarray(start, start + 32768));
      return btoa(text);
    };
    return {
      size: [width, height],
      webp: await toBase64(await canvas.convertToBlob({ type: 'image/webp', quality: 0.9 })),
      jpeg: await toBase64(await canvas.convertToBlob({ type: 'image/jpeg', quality: 0.92 })),
    };
  }, {
    webp: readFileSync(source).toString('base64'),
    settings: { paper: PAPER_LIGHTNESS, span: LINE_SPAN, curve: CURVE, strength: STRENGTH, ground: GROUND, bloom: BLOOM },
  });
  writeFileSync(outputs.webp, Buffer.from(result.webp, 'base64'));
  writeFileSync(outputs.jpeg, Buffer.from(result.jpeg, 'base64'));
  console.log(`print ${result.size.join('x')} -> ${outputs.webp}, ${outputs.jpeg}`);
} finally {
  await browser.close();
}
