// 回归测试：文字不能丢、不能写错记录。npm run test:drafts
// 这些场景都来自代码审查发现的真实缺陷，只有快速连续操作才会触发，靠人工点不出来。
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5185, strictPort: true }, logLevel: 'error' });
await server.listen();
const candidates = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'];
const browser = await chromium.launch({ headless: true, executablePath: candidates.find((path) => existsSync(path)), args: ['--use-angle=d3d11', '--use-gl=angle', '--enable-gpu', '--ignore-gpu-blocklist'] });

// QS_SKIN=crabapple 时在海棠下跑一遍
const skin = process.env.QS_SKIN ?? 'starlake';
let failures = 0;
function check(name, passed, detail = '') {
  console.log(`${passed ? '通过' : '失败'}  ${name}${detail ? `  ${detail}` : ''}`);
  if (!passed) failures += 1;
}

async function open(query = 'records=8&day=3') {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('pageerror', (error) => { failures += 1; console.log('页面错误', error.message); });
  await page.goto(`http://127.0.0.1:5185/?state=normal&skin=${skin}&${query}`);
  await page.waitForFunction(() => document.body.dataset.appReady === 'true');
  // 记录所有写操作，直接验证发给原生层的内容
  await page.evaluate(() => {
    window.__writes = [];
    window.__mockFailUpdate = false;
  });
  return page;
}
const item = (page, index) => page.locator('.record-item').nth(index);
const itemText = (page, index) => item(page, index).locator('.record-text').innerText();
const editor = (page) => page.locator('.inspector-editor');

try {
  {
    const page = await open();
    const a = await itemText(page, 0), b = await itemText(page, 1);
    await item(page, 0).click();
    await editor(page).press('End');
    await editor(page).pressSequentially('AAA');
    await item(page, 1).click();                     // 不等防抖，立刻切换
    await editor(page).press('End');
    await editor(page).pressSequentially('BBB');
    await page.waitForTimeout(900);
    const a2 = await itemText(page, 0), b2 = await itemText(page, 1);
    check('改 A 后立刻切到 B，A 的文字保留', a2 === `${a}AAA`, a2);
    check('B 的文字没有写进 A', !a2.includes('BBB') && b2 === `${b}BBB`, b2);
    await item(page, 0).click();
    await page.waitForTimeout(100);
    check('切回 A，编辑器显示 A 的文字', (await editor(page).inputValue()) === `${a}AAA`);
    await page.close();
  }
  {
    const page = await open();
    await page.evaluate(() => { navigator.clipboard.writeText = (text) => { window.__clip = text; return Promise.resolve(); }; });
    await item(page, 0).click();
    await editor(page).press('End');
    await editor(page).pressSequentially('刚打的字');
    await page.locator('.copy-ai').click();          // 不等防抖，立刻复制
    await page.waitForTimeout(300);
    const clip = await page.evaluate(() => window.__clip ?? '');
    check('输入后立刻复制，内容含最新文字', clip.includes('刚打的字'), clip.split('\n')[1] ?? '');
    await page.close();
  }
  {
    const page = await open();
    const original = await itemText(page, 0);
    await item(page, 0).click();
    await editor(page).press('End');
    await editor(page).pressSequentially('关窗前');
    await page.locator('.close-button').click();
    await page.waitForTimeout(300);
    check('输入后立刻关窗，文字已保存', (await itemText(page, 0)) === `${original}关窗前`);
    await page.close();
  }
  {
    const page = await open();
    const first = await itemText(page, 0);
    await item(page, 0).click();
    await page.locator('#history-search').fill('开关');
    await page.waitForTimeout(450);
    check('搜索把已选记录过滤掉后，舞台仍然打开', await page.locator('.inspector').isVisible());
    check('今天的星数不受搜索影响', (await page.locator('#star-count').innerText()) === '8');
    check('已选计数正确', (await page.locator('.selection-summary').innerText()) === '已选 1 条');
    await page.locator('#history-search').fill('');
    await page.waitForTimeout(450);
    check('清空搜索后记录还在', (await itemText(page, 0)) === first);
    await page.close();
  }
  {
    const page = await open();
    await item(page, 2).click();
    await item(page, 0).click({ modifiers: ['Control'] });
    await page.waitForTimeout(150);
    const times = await page.locator('.selected-list time').allInnerTexts();
    const listTimes = await page.locator('.record-time').allInnerTexts();
    check('多选按选择顺序，不是列表顺序', times[0] === listTimes[2] && times[1] === listTimes[0], times.join(' / '));
    await page.close();
  }
  if (skin === 'starlake') {
    const page = await open('records=8&day=3&webgl=0');
    const overlay = page.locator('.constellation-fallback');
    check('回退模式的星座画布存在', (await overlay.count()) === 1);
    const pointerEvents = await overlay.evaluate((node) => getComputedStyle(node).pointerEvents);
    check('回退模式的星座能接收点击', pointerEvents !== 'none', pointerEvents);
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
console.log(failures ? `\n${failures} 项失败` : '\n全部通过');
process.exit(failures ? 1 : 0);
