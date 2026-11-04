import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const output = join(root, 'src', 'QuanShuo.Native', 'Assets', 'quanshuo.ico');
const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc32 = (buffer) => {
  let c = 0xffffffff;
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};
const chunk = (name, data) => {
  const type = Buffer.from(name);
  const result = Buffer.alloc(12 + data.length);
  result.writeUInt32BE(data.length, 0);
  type.copy(result, 4);
  data.copy(result, 8);
  result.writeUInt32BE(crc32(Buffer.concat([type, data])), 8 + data.length);
  return result;
};
const star = Array.from({ length: 8 }, (_, i) => {
  const angle = i * Math.PI / 4 - Math.PI / 2;
  const radius = i % 2 === 0 ? 11 : 2.2;
  return [49.5 + Math.cos(angle) * radius, 18.6 + Math.sin(angle) * radius];
});
const insideStar = (x, y) => {
  let inside = false;
  for (let i = 0, j = star.length - 1; i < star.length; j = i++) {
    const [xi, yi] = star[i], [xj, yj] = star[j];
    if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
};
const insideRing = (x, y) => {
  const dx = x - 32, dy = y - 32;
  const angle = Math.atan2(dy, dx) * 180 / Math.PI;
  if ((angle <= -60 || angle >= -15) && Math.abs(Math.hypot(dx, dy) - 22) <= 2.5) return true;
  return [-60, -15].some(degrees => {
    const radians = degrees * Math.PI / 180;
    return Math.hypot(dx - 22 * Math.cos(radians), dy - 22 * Math.sin(radians)) <= 2.5;
  });
};
const insideBackground = (x, y) => {
  const dx = Math.max(Math.abs(x - 32) - 18, 0);
  const dy = Math.max(Math.abs(y - 32) - 18, 0);
  return Math.hypot(dx, dy) <= 14;
};
const draw = (size) => {
  const pixels = Buffer.alloc(size * size * 4);
  // 在子像素上覆盖采样，16px 的断环与星尖也能保持相同的几何比例。
  const samples = 4;
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    let covered = 0;
    const sum = [0, 0, 0];
    for (let sy = 0; sy < samples; sy++) for (let sx = 0; sx < samples; sx++) {
      const u = (x + (sx + .5) / samples) / size * 64;
      const v = (y + (sy + .5) / samples) / size * 64;
      if (!insideBackground(u, v)) continue;
      const color = insideRing(u, v) || insideStar(u, v) ? [238, 243, 255] : [6, 11, 31];
      covered++;
      for (let c = 0; c < 3; c++) sum[c] += color[c];
    }
    const index = (y * size + x) * 4;
    for (let c = 0; c < 3; c++) pixels[index + c] = covered ? Math.round(sum[c] / covered) : 0;
    pixels[index + 3] = Math.round(covered / (samples * samples) * 255);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6;
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) pixels.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
};
const sizes = [16, 20, 24, 32, 48, 256];
const images = sizes.map(draw);
const header = Buffer.alloc(6);
header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
const entries = Buffer.alloc(16 * sizes.length);
let offset = 6 + entries.length;
images.forEach((image, index) => {
  entries[index * 16] = sizes[index] === 256 ? 0 : sizes[index];
  entries[index * 16 + 1] = sizes[index] === 256 ? 0 : sizes[index];
  entries.writeUInt16LE(1, index * 16 + 4);
  entries.writeUInt16LE(32, index * 16 + 6);
  entries.writeUInt32LE(image.length, index * 16 + 8);
  entries.writeUInt32LE(offset, index * 16 + 12);
  offset += image.length;
});
const arcPoint = degrees => [32 + 22 * Math.cos(degrees * Math.PI / 180), 32 + 22 * Math.sin(degrees * Math.PI / 180)].map(n => n.toFixed(4)).join(' ');
const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" fill="none">
  <rect width="64" height="64" rx="14" fill="#060B1F"/>
  <path d="M ${arcPoint(-15)} A 22 22 0 1 1 ${arcPoint(-60)}" stroke="#EEF3FF" stroke-width="5" stroke-linecap="round"/>
  <path d="M ${star.map(point => point.map(n => n.toFixed(4)).join(' ')).join(' L ')} Z" fill="#EEF3FF"/>
</svg>
`;
writeFileSync(output, Buffer.concat([header, entries, ...images]));
writeFileSync(output.replace(/\.ico$/, '.png'), images.at(-1));
writeFileSync(join(dirname(output), 'quanshuo-icon.svg'), svg);
console.log(`Generated ${output} (${sizes.join(', ')} px), PNG and SVG`);
