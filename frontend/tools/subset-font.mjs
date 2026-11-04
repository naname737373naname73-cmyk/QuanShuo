// 从系统 Noto Serif SC 可变字体裁出界面用字，输出 public/assets/fonts。
// 字体许可是 SIL OFL 1.1，随附 LICENSE 文件。改界面文案后重新运行：node tools/subset-font.mjs
// 海棠的标题和按钮都用这个字体。别的电脑没装 Noto Serif SC，缺的字会退回微软雅黑，所以把界面文案全部裁进来。
// 用户说的话内容不固定，不用这个字体。
import subsetFont from 'subset-font';
import { readFile, readdir, stat, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(frontend, 'public/assets/fonts');
await mkdir(output, { recursive: true });

async function sources(directory) {
  const found = [];
  for (const name of await readdir(directory)) {
    const path = resolve(directory, name);
    if ((await stat(path)).isDirectory()) found.push(...await sources(path));
    else if (name.endsWith('.ts') && name !== 'mockBridge.ts') found.push(path);
  }
  return found;
}

const withoutComments = (code) => code.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/.*$/gm, '');
const chars = new Set('，。、：；？！（）“”《》·… :0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz+-/.%');
for (const file of await sources(resolve(frontend, 'src'))) {
  for (const char of withoutComments(await readFile(file, 'utf8'))) {
    if (char >= '\u4e00' && char <= '\u9fff') chars.add(char);
  }
}
// 星期和日期
for (const char of '一二三四五六七八九十日月年周') chars.add(char);

const text = [...chars].join('');
const source = await readFile('C:/Windows/Fonts/NotoSerifSC-VF.ttf');
const subset = await subsetFont(source, text, { targetFormat: 'woff2' });
await writeFile(resolve(output, 'noto-serif-sc-subset.woff2'), subset);
console.log(`${text.length} 字，${(subset.length / 1024).toFixed(1)} KB`);
