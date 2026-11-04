// 批量选择（复制、删除）和日历的回归测试。npm run test:manage
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5186, strictPort: true }, logLevel: 'error' });
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
  await page.goto(`http://127.0.0.1:5186/?state=normal&skin=${skin}&${query}`);
  await page.waitForFunction(() => document.body.dataset.appReady === 'true');
  return page;
}
const item = (page, index) => page.locator('.record-item').nth(index);
const count = (page) => page.locator('.record-item').count();
const marked = (page) => page.locator('.record-item[aria-pressed="true"]').count();
const label = (page) => page.locator('.manage-delete').innerText();

try {
  {
    const page = await open();
    const before = await count(page);
    await page.locator('.manage-button').click();
    check('进入挑选后出现勾选框', await item(page, 0).locator('.record-check').isVisible());
    check('底栏换成删除操作', await page.locator('.manage-delete').isVisible() && !(await page.locator('.copy-ai').isVisible()));
    check('没勾选时不能删', await page.locator('.manage-delete').isDisabled());

    await item(page, 0).click();
    await item(page, 2).click();
    check('点两条勾上两条', await marked(page) === 2);
    check('勾选不打开详情', await page.locator('.inspector').isHidden());
    check('按钮显示条数', await label(page) === '删除 2 条', await label(page));

    await item(page, 0).click();
    check('再点一次取消勾选', await marked(page) === 1);
    await item(page, 0).click();

    await page.locator('.manage-delete').click();
    check('第一下只是确认', await count(page) === before && await label(page) === '再点一次确认', await label(page));
    await page.locator('.manage-delete').click();
    await page.waitForFunction((expected) => document.querySelectorAll('.record-item').length === expected, before - 2);
    check('第二下删掉两条', await count(page) === before - 2);
    check('删完退出挑选', await page.locator('.record-check').first().isHidden());
    check('撤销提示出现', await page.locator('.toast button').isVisible());

    await page.locator('.toast button').click();
    await page.waitForFunction((expected) => document.querySelectorAll('.record-item').length === expected, before);
    check('撤销后两条都回来', await count(page) === before);
    await page.close();
  }

  {
    const page = await open();
    await page.locator('.manage-button').click();
    await item(page, 1).click();
    await item(page, 4).click({ modifiers: ['Shift'] });
    check('Shift 连选一段', await marked(page) === 4, String(await marked(page)));
    await page.keyboard.press('Control+A');
    check('Ctrl+A 全选', await marked(page) === await count(page));
    check('全选按钮变成全不选', await page.locator('.manage-all').innerText() === '全不选');
    await page.locator('.manage-all').click();
    check('再点全不选', await marked(page) === 0);
    await page.locator('.manage-incomplete').click();
    check('一键选未转完', await marked(page) === 1, String(await marked(page)));
    await page.keyboard.press('Escape');
    check('Esc 退出挑选', await page.locator('.record-check').first().isHidden());
    check('退出后勾选清空', await marked(page) === 0);
    await item(page, 0).click();
    check('退出后点击恢复打开详情', await page.locator('.inspector').isVisible());
    await page.close();
  }

  {
    const page = await open();
    await item(page, 0).click();
    check('先打开一条详情', await page.locator('.inspector').isVisible());
    await page.locator('.manage-button').click();
    check('进入挑选时收起详情', await page.locator('.inspector').isHidden());
    await page.close();
  }

  {
    const page = await open();
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
    const clipboard = () => page.evaluate(async () => (await navigator.clipboard.readText()).replaceAll('\r\n', '\n'));
    const clipboardChanged = async () => {
      // waitForFunction 不等异步判断，这里自己轮询
      for (let tries = 0; tries < 40 && await clipboard() === '原来的内容'; tries += 1) await page.waitForTimeout(25);
    };
    const seed = () => page.evaluate(() => navigator.clipboard.writeText('原来的内容'));
    await seed();

    await page.locator('.manage-button').click();
    check('没勾选时不能复制', await page.locator('.manage-copy-ai').isDisabled() && await page.locator('.manage-copy-text').isDisabled());
    await page.keyboard.press('Control+C');
    await page.waitForTimeout(100);
    check('没勾选时 Ctrl+C 不动剪贴板', await clipboard() === '原来的内容', await clipboard());

    const texts = await page.locator('.record-item .record-text').allInnerTexts().catch(() => []);
    await item(page, 2).click();
    await item(page, 0).click();
    check('显示已选条数', await page.locator('.manage-count').innerText() === '已选 2 条', await page.locator('.manage-count').innerText());

    await seed();
    await page.locator('.manage-copy-ai').click();
    await clipboardChanged();
    const forAi = await clipboard();
    check('复制给 AI 带编号', forAi.startsWith('[1]\n') && forAi.includes('\n\n[2]\n') && !forAi.includes('[3]'), forAi.slice(0, 40));
    check('复制后还在批量选择', await marked(page) === 2 && await page.locator('.record-check').first().isVisible());

    await seed();
    await page.locator('.manage-copy-text').click();
    await clipboardChanged();
    check('提示复制了几条文字', (await page.locator('.toast').innerText()).includes('已复制 2 条文字'), await page.locator('.toast').innerText());
    const plain = await clipboard();
    check('只复制文字没有格式', plain.split('\n\n').length === 2 && !plain.includes('[说明]') && !plain.includes('[时间]'), plain.slice(0, 60));
    check('按勾选顺序排列', texts.length === 0 || plain.startsWith(texts[2].slice(0, 6)), plain.slice(0, 30));

    await seed();
    await page.keyboard.press('Control+C');
    await clipboardChanged();
    check('Ctrl+C 复制勾选的记录', (await clipboard()).startsWith('[1]\n'), (await clipboard()).slice(0, 30));

    const box = await page.locator('.manage-foot').boundingBox();
    const inside = await page.evaluate(() => [...document.querySelectorAll('.manage-foot button')].every((button) => button.scrollWidth <= button.clientWidth + 1));
    await page.locator('.manage-delete').click();
    const confirming = await page.evaluate(() => [...document.querySelectorAll('.manage-foot button')].every((button) => button.scrollWidth <= button.clientWidth + 1));
    check('底栏按钮文字放得下', inside && confirming && box.width <= 368);
    await page.close();
  }

  {
    const page = await open();
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.evaluate(() => navigator.clipboard.writeText('原来的内容'));
    const read = () => page.evaluate(async () => (await navigator.clipboard.readText()).replaceAll('\r\n', '\n'));
    const editor = page.locator('.inspector-editor');
    await item(page, 0).click();
    const kept = await editor.inputValue();
    await editor.fill('');
    await page.waitForFunction(() => document.querySelector('.record-item .record-text.placeholder'));
    await page.keyboard.press('Escape');
    await page.locator('.manage-button').click();

    await item(page, 0).click();
    await page.locator('.manage-copy-text').click();
    await page.waitForFunction(() => document.querySelector('.toast')?.textContent?.includes('没有文字'));
    check('全是空记录时不动剪贴板', await read() === '原来的内容', await read());

    await item(page, 1).click();
    const second = (await item(page, 1).locator('.record-text').innerText()).slice(0, 6);
    await page.locator('.manage-copy-text').click();
    await page.waitForFunction(() => document.querySelector('.toast')?.textContent?.includes('跳过了'));
    const value = await read();
    check('空记录被跳过', value.startsWith(second) && !value.includes('\n\n') && kept.length > 0, value.slice(0, 30));
    check('提示跳过了几条', (await page.locator('.toast').innerText()).includes('已复制 1 条文字，1 条没有文字，跳过了'), await page.locator('.toast').innerText());
    await page.close();
  }

  {
    const page = await open();
    await page.locator('#history-search').fill('颜色');
    await page.waitForTimeout(400);
    const found = await count(page);
    await page.locator('.manage-button').click();
    await page.locator('.manage-all').click();
    check('搜索时全选只选搜到的', found > 0 && await marked(page) === found, `${found}`);
    await page.close();
  }

  {
    const page = await open();
    await page.locator('.date-jump').click();
    check('日历打开', await page.locator('.calendar-pop').isVisible());
    const title = await page.locator('.calendar-pop [data-month]').getAttribute('data-month');
    const now = new Date();
    check('显示当前月份', title === `${now.getFullYear()} 年 ${now.getMonth() + 1} 月`, title);
    check('有记录的日子能点', await page.locator('.cal-day[data-day]').count() >= 1);
    check('有记录的日子有记号', await page.locator('.cal-day[data-day] img, .cal-day[data-day] .cal-star').count() >= 1);
    check('今天被标出来', await page.locator('.cal-day[data-today="true"]').count() === 1);
    check('没记录的日子不能点', await page.locator('.cal-day:not([data-day])').first().isDisabled());
    const dayCount = await page.locator('.cal-day').count();
    check(process.env.QS_SKIN === 'crabapple' ? '花框里一个月固定 42 格' : '星图只画这个月的日子', process.env.QS_SKIN === 'crabapple' ? dayCount === 42 : dayCount === new Date(now.getFullYear(), now.getMonth() + 1, 0).getDate(), String(dayCount));

    await page.locator('.cal-nav').first().click();
    const previous = new Date(now.getFullYear(), now.getMonth() - 1, 1);
    check('切到上个月', await page.locator('.calendar-pop [data-month]').getAttribute('data-month') === `${previous.getFullYear()} 年 ${previous.getMonth() + 1} 月`);
    await page.locator('.cal-today').click();
    check('回到今天', await page.locator('.calendar-pop [data-month]').getAttribute('data-month') === title);

    await page.keyboard.press('Escape');
    check('Esc 关闭日历', await page.locator('.calendar-pop').isHidden());
    await page.locator('.date-jump').click();
    await page.mouse.click(1000, 800);
    check('点外面关闭日历', await page.locator('.calendar-pop').isHidden());
    await page.close();
  }

  {
    const page = await open();
    await page.locator('#history-search').fill('没有这句话的内容');
    await page.waitForTimeout(400);
    await page.locator('.date-jump').click();
    check('搜索时日历仍标全部记录', await page.locator('.cal-day[data-day]').count() >= 1);
    await page.locator('.cal-day[data-day]').first().click();
    await page.waitForFunction(() => document.querySelectorAll('.record-item').length > 0);
    check('点日期会清空搜索', await page.locator('#history-search').inputValue() === '');
    check('点日期后日历关闭', await page.locator('.calendar-pop').isHidden());
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
console.log(failures === 0 ? '全部通过' : `${failures} 项失败`);
process.exit(failures === 0 ? 0 : 1);
