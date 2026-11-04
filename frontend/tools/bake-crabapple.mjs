// 海棠皮肤的画：node tools/bake-crabapple.mjs [blue]
// 不带参数：输入 art/crabapple/roundel-pink.png（GPT 画的原图），在 Chrome 里处理，产出 public/assets/crabapple/：
//   colour.webp    上色的整幅画
//   ink.webp       同一幅画的淡墨稿，位置和上色版逐像素对齐
//   flowers.webp   切下来的花，集中放在一张图上
//   src/config/crabapple-flowers.json   每朵花的位置，按树干往枝梢的顺序排，记录按这个顺序开花
// 带 blue：输入 art/crabapple/roundel-blue.png（水边那幅，花开满以后点空白处换），只产出
//   colour-blue.webp 和 src/config/crabapple-flowers-blue.json（每朵花的位置，同一条记录在两幅画里各对应一朵）
// 花的种子位置是人看着画定的。换画以后要重新定。
import { chromium } from 'playwright';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const blue = process.argv[2] === 'blue';
const source = resolve(frontend, `art/crabapple/roundel-${blue ? 'blue' : 'pink'}.png`);
const output = resolve(frontend, 'public/assets/crabapple');
const debugDir = process.env.QS_BAKE_DEBUG;
mkdirSync(output, { recursive: true });

// [x, y, 半径]：花心位置和花大约多大。顺序就是开花的顺序。
const SEEDS = [
  [155, 800, 100], [282, 630, 108], [120, 445, 100], [210, 295, 106], [400, 482, 104],
  [470, 910, 98], [650, 515, 86], [650, 230, 90], [560, 115, 80], [665, 1130, 62],
  [962, 482, 58], [720, 330, 62], [875, 82, 88], [1122, 340, 76], [268, 165, 62],
];
// 水边那幅，坐标按 1254 见方算。数量必须和上面一样，每条记录在两幅画里都有花
const BLUE_SEEDS = [
  [1128, 498, 80], [1041, 596, 76], [928, 472, 84], [878, 708, 80], [1160, 820, 62],
  [1068, 962, 66], [915, 1070, 80], [600, 1115, 70], [823, 236, 64], [517, 170, 82],
  [370, 90, 72], [352, 312, 70], [241, 185, 64], [193, 303, 64], [207, 398, 52],
];
if (BLUE_SEEDS.length !== SEEDS.length) throw new Error('两幅画的花数不一样');
// 两幅画用同一套坐标
const SIZE = 1254;

const chromePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
const browser = await chromium.launch({ headless: true, executablePath: chromePath });
const page = await browser.newPage();
await page.setContent('<canvas id="c"></canvas>');
const png = readFileSync(source).toString('base64');

const result = await page.evaluate(async ({ png, seeds, debug, blue, size }) => {
  const image = new Image();
  image.src = `data:image/png;base64,${png}`;
  await image.decode();
  const canvas = new OffscreenCanvas(size, size);
  const context = canvas.getContext('2d', { willReadFrequently: true });
  context.imageSmoothingQuality = 'high';
  context.drawImage(image, 0, 0, size, size);
  const pixels = context.getImageData(0, 0, size, size).data;
  const index = (x, y) => (y * size + x) * 4;

  const toBase64 = async (blob) => {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let text = '';
    for (let start = 0; start < bytes.length; start += 32768) text += String.fromCharCode(...bytes.subarray(start, start + 32768));
    return btoa(text);
  };
  const encode = async (surface, quality) => toBase64(await surface.convertToBlob({ type: 'image/webp', quality }));

  // —— 淡墨稿：明暗留一层淡淡的水墨，边缘描成线，粉色底退成纸色 ——
  const luminance = new Float32Array(size * size);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const i = index(x, y);
      luminance[y * size + x] = (0.2126 * pixels[i] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i + 2]) / 255;
    }
  }
  const blurred = new Float32Array(size * size);
  for (let y = 1; y < size - 1; y++) {
    for (let x = 1; x < size - 1; x++) {
      let sum = 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) sum += luminance[(y + dy) * size + x + dx];
      blurred[y * size + x] = sum / 9;
    }
  }
  const paper = [246, 237, 218], ink = [96, 74, 56];
  const inkPixels = new ImageData(size, size);
  for (let y = 1; y < size - 1; y++) {
    for (let x = 1; x < size - 1; x++) {
      const p = y * size + x;
      const gx = blurred[p + 1] - blurred[p - 1], gy = blurred[p + size] - blurred[p - size];
      const edge = Math.min(1, Math.hypot(gx, gy) * 5.2);
      const wash = Math.max(0, Math.min(1, (0.72 - blurred[p]) / 0.5));
      const amount = Math.min(0.82, Math.max(wash * 0.5, edge * edge * 0.95));
      const o = p * 4;
      for (let channel = 0; channel < 3; channel++) inkPixels.data[o + channel] = paper[channel] + (ink[channel] - paper[channel]) * amount;
      inkPixels.data[o + 3] = 255;
    }
  }
  const inkCanvas = new OffscreenCanvas(size, size);
  const inkContext = inkCanvas.getContext('2d');
  inkContext.putImageData(inkPixels, 0, 0);
  // 算边缘时最外一圈像素没有邻居，还是黑的，用里面一圈盖掉
  inkContext.drawImage(inkCanvas, 1, 1, size - 2, size - 2, 0, 0, size, size);

  // —— 切花：从花心往外找花瓣色的连通区域，补洞，再羽化一点边 ——
  const isPetal = (x, y) => {
    const i = index(x, y);
    const r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
    // 水边那幅的底是青绿的水，绿和蓝都比红高；花瓣从奶白到粉红，红最高
    if (blue) return r > 196 && r > g + 6 && r > b + 14;
    // 底色偏橙，绿比蓝高得多；花瓣从奶白到粉红，绿和蓝差不多。只看红和亮度的话，花瓣尖的粉色会被当成底色
    // 奶白的花瓣偏黄，绿蓝差也大，但比底色亮得多，单独放行
    return (r > 176 && g > 112 && g - b < 25) || (r > 224 && g > 200);
  };
  const flowers = [];
  const masks = [];
  for (const [cx, cy, radius] of seeds) {
    const left = Math.max(0, cx - radius - 6), top = Math.max(0, cy - radius - 6);
    const width = Math.min(size, cx + radius + 6) - left, height = Math.min(size, cy + radius + 6) - top;
    const seen = new Uint8Array(width * height);
    const stack = [];
    const inDisc = (x, y) => Math.hypot(x - cx, y - cy) <= radius;
    const coreRadius = radius * 0.62;
    for (let y = Math.ceil(cy - coreRadius); y <= cy + coreRadius; y++) {
      for (let x = Math.ceil(cx - coreRadius); x <= cx + coreRadius; x++) {
        if (x < 0 || y < 0 || x >= size || y >= size) continue;
        if (Math.hypot(x - cx, y - cy) <= coreRadius && isPetal(x, y)) stack.push(x, y);
      }
    }
    while (stack.length) {
      const y = stack.pop(), x = stack.pop();
      if (x < left || y < top || x >= left + width || y >= top + height) continue;
      const k = (y - top) * width + (x - left);
      if (seen[k] || !inDisc(x, y) || !isPetal(x, y)) continue;
      seen[k] = 1;
      stack.push(x + 1, y, x - 1, y, x, y + 1, x, y - 1);
    }
    // 闭运算：把花蕊和花瓣之间的细缝连起来
    const dilate = (input, r) => {
      const out = new Uint8Array(input.length);
      for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        if (!input[y * width + x]) continue;
        for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
          const nx = x + dx, ny = y + dy;
          if (nx >= 0 && ny >= 0 && nx < width && ny < height && dx * dx + dy * dy <= r * r) out[ny * width + nx] = 1;
        }
      }
      return out;
    };
    const erode = (input, r) => {
      const inverted = input.map((value) => 1 - value);
      return dilate(inverted, r).map((value) => 1 - value);
    };
    // 花心一圈直接算进去：花蕊是深金色，花瓣没围严时补洞补不到它
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      if (Math.hypot(left + x - cx, top + y - cy) <= radius * 0.3) seen[y * width + x] = 1;
    }
    let mask = erode(dilate(seen, 7), 7);
    // 补洞：从外边界灌水，灌不到的就是花蕊
    const outside = new Uint8Array(width * height);
    const flood = [];
    for (let x = 0; x < width; x++) flood.push(x, 0, x, height - 1);
    for (let y = 0; y < height; y++) flood.push(0, y, width - 1, y);
    while (flood.length) {
      const y = flood.pop(), x = flood.pop();
      if (x < 0 || y < 0 || x >= width || y >= height) continue;
      const k = y * width + x;
      if (outside[k] || mask[k]) continue;
      outside[k] = 1;
      flood.push(x + 1, y, x - 1, y, x, y + 1, x, y - 1);
    }
    mask = mask.map((value, k) => (value || !outside[k] ? 1 : 0));
    mask = dilate(mask, 2);

    let area = 0, minX = width, minY = height, maxX = 0, maxY = 0;
    mask.forEach((value, k) => {
      if (!value) return;
      area++;
      const x = k % width, y = Math.floor(k / width);
      minX = Math.min(minX, x); minY = Math.min(minY, y); maxX = Math.max(maxX, x); maxY = Math.max(maxY, y);
    });
    masks.push({ mask, left, top, width, height });
    flowers.push({ cx, cy, radius, area, box: [left + minX, top + minY, maxX - minX + 1, maxY - minY + 1], fill: area / (Math.PI * radius * radius) });
  }

  // —— 拼成一张图：花放在格子里，带羽化的透明边 ——
  const pad = 6;
  const atlasWidth = 1024;
  let cursorX = 0, cursorY = 0, rowHeight = 0;
  flowers.forEach((flower) => {
    const [, , w, h] = flower.box;
    if (cursorX + w + pad * 2 > atlasWidth) { cursorX = 0; cursorY += rowHeight; rowHeight = 0; }
    flower.atlas = [cursorX + pad, cursorY + pad];
    cursorX += w + pad * 2;
    rowHeight = Math.max(rowHeight, h + pad * 2);
  });
  const atlasHeight = cursorY + rowHeight;
  const atlas = new OffscreenCanvas(atlasWidth, atlasHeight);
  const atlasContext = atlas.getContext('2d');
  flowers.forEach((flower, position) => {
    const { mask, left, top, width, height } = masks[position];
    const maskCanvas = new OffscreenCanvas(width, height);
    const maskPixels = new ImageData(width, height);
    mask.forEach((value, k) => { maskPixels.data[k * 4 + 3] = value ? 255 : 0; });
    maskCanvas.getContext('2d').putImageData(maskPixels, 0, 0);
    const [boxX, boxY, boxW, boxH] = flower.box;
    const sprite = new OffscreenCanvas(boxW + pad * 2, boxH + pad * 2);
    const spriteContext = sprite.getContext('2d');
    spriteContext.filter = 'blur(1.6px)';
    spriteContext.drawImage(maskCanvas, left - boxX + pad, top - boxY + pad);
    spriteContext.filter = 'none';
    spriteContext.globalCompositeOperation = 'source-in';
    spriteContext.drawImage(canvas, -boxX + pad, -boxY + pad);
    atlasContext.drawImage(sprite, flower.atlas[0] - pad, flower.atlas[1] - pad);
  });

  const json = {
    size,
    atlas: [atlasWidth, atlasHeight],
    flowers: flowers.map((flower) => ({
      x: flower.cx, y: flower.cy, r: Math.round(Math.sqrt(flower.area / Math.PI)),
      box: [flower.box[0] - pad, flower.box[1] - pad, flower.box[2] + pad * 2, flower.box[3] + pad * 2],
      at: [flower.atlas[0] - pad, flower.atlas[1] - pad],
    })),
  };
  const report = flowers.map((flower) => `${flower.cx},${flower.cy} fill=${flower.fill.toFixed(2)} box=${flower.box.join('x')}`);

  let preview = null;
  if (debug) {
    const check = new OffscreenCanvas(size, size);
    const checkContext = check.getContext('2d');
    checkContext.drawImage(inkCanvas, 0, 0);
    json.flowers.forEach((flower) => checkContext.drawImage(atlas, ...flower.at, flower.box[2], flower.box[3], flower.box[0], flower.box[1], flower.box[2], flower.box[3]));
    preview = await toBase64(await check.convertToBlob({ type: 'image/jpeg', quality: 0.9 }));
  }
  return {
    json, report, preview,
    colour: await encode(canvas, 0.86),
    ink: await encode(inkCanvas, 0.82),
    atlas: await encode(atlas, 0.86),
  };
}, { png, seeds: blue ? BLUE_SEEDS : SEEDS, debug: Boolean(debugDir), blue, size: SIZE });

const write = (name, base64) => writeFileSync(resolve(output, name), Buffer.from(base64, 'base64'));
if (blue) {
  // 水边那幅不用淡墨稿和切下来的花，只要整幅画和花的位置
  write('colour-blue.webp', result.colour);
  const flowers = result.json.flowers.map(({ x, y, r, box }) => ({ x, y, r, box }));
  writeFileSync(resolve(frontend, 'src/config/crabapple-flowers-blue.json'), `${JSON.stringify({ size: result.json.size, flowers })}\n`);
} else {
  write('colour.webp', result.colour);
  write('ink.webp', result.ink);
  write('flowers.webp', result.atlas);
  writeFileSync(resolve(frontend, 'src/config/crabapple-flowers.json'), `${JSON.stringify(result.json)}\n`);
}
if (debugDir && result.preview) writeFileSync(resolve(debugDir, `bake-check${blue ? '-blue' : ''}.jpg`), Buffer.from(result.preview, 'base64'));
console.log(result.report.join('\n'));
await browser.close();
