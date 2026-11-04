// 两套皮肤：npm run test:skins
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5192, strictPort: true }, logLevel: 'error' });
await server.listen();
const executablePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
const browser = await chromium.launch({ headless: true, executablePath, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
const base = 'http://127.0.0.1:5192/';
let failed = 0;
const check = (name, passed, detail = '') => {
  if (!passed) failed += 1;
  console.log(`${passed ? 'PASS' : 'FAIL'} ${name}${passed || !detail ? '' : `：${detail}`}`);
};

async function open(query, size = { width: 1366, height: 800 }) {
  const page = await browser.newPage({ viewport: size });
  const errors = [];
  const requested = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('request', (request) => requested.push(request.url()));
  page.on('response', (response) => { if (response.status() >= 400 && !response.url().endsWith('favicon.ico')) errors.push(`${response.status()} ${response.url()}`); });
  await page.goto(`${base}?${query}`);
  await page.waitForFunction(() => document.body.dataset.appReady === 'true');
  return { page, errors, requested };
}

/** 圆画上的一个位置有多“彩”：淡墨稿的 R 和 B 差不多，上了色的花瓣差得多。 */
const saturationAt = (page, x, y) => page.evaluate(({ x, y }) => {
  const canvas = document.querySelector('.bloom');
  const scale = canvas.width / 1254;
  const pixels = canvas.getContext('2d').getImageData(Math.round(x * scale) - 12, Math.round(y * scale) - 12, 24, 24).data;
  let total = 0;
  for (let index = 0; index < pixels.length; index += 4) total += pixels[index] - pixels[index + 2];
  return total / (pixels.length / 4);
}, { x, y });

// 第 1 朵和最后一朵花瓣上的点，避开花蕊
const FIRST_PETAL = [110, 800], LAST_PETAL = [240, 150];
// 两幅画里都没有花的空白处；水边那幅在这里是青绿的水
const BLANK = [450, 700];
// 水边那幅里第 1 朵花的花心
const BLUE_FIRST = [1128, 500];

/** 在圆画上按原画坐标点一下。 */
async function clickPainting(page, [x, y]) {
  const canvas = await page.locator('.bloom').boundingBox();
  await page.mouse.click(canvas.x + x / 1254 * canvas.width, canvas.y + y / 1254 * canvas.height);
}

try {
  {
    const { page, errors, requested } = await open('skin=crabapple&state=normal&records=8&t=1');
    await page.waitForFunction(() => document.body.dataset.sceneReady === 'true');
    await page.waitForTimeout(300);
    check('海棠没有报错', errors.length === 0, errors.join(' | '));
    check('海棠的标题', (await page.locator('.today-title').innerText()).replace(/\s/g, '') === '今天开了8朵花');
    check('海棠不启动 3D 湖', await page.locator('.lake').count() === 0);
    check('海棠不下载湖的地形图', !requested.some((url) => url.includes('terrain')));
    check('海棠没有时间滑块', !(await page.locator('.day-control').isVisible()));
    const opened = await saturationAt(page, ...FIRST_PETAL);
    const closed = await saturationAt(page, ...LAST_PETAL);
    check('有记录的花上了色', opened > closed + 25, `第一朵 ${opened.toFixed(1)}，末尾 ${closed.toFixed(1)}`);

    const canvas = await page.locator('.bloom').boundingBox();
    const point = { x: canvas.x + 155 / 1254 * canvas.width, y: canvas.y + 800 / 1254 * canvas.height };
    await page.mouse.move(point.x, point.y);
    await page.waitForFunction(() => !document.querySelector('.star-tip').hidden);
    check('移到花上显示那条记录', (await page.locator('.star-tip').innerText()).length > 4);
    await page.mouse.move(canvas.x + canvas.width * .78, canvas.y + canvas.height * .8);
    await page.waitForFunction(() => document.querySelector('.star-tip').hidden);
    check('移开提示消失', true);

    await page.mouse.click(point.x, point.y);
    await page.waitForSelector('.inspector:not([hidden])');
    check('点花打开那条记录', true);
    await page.waitForFunction(() => document.querySelector('.garden').dataset.receded === 'true');
    check('选中截图时圆画退下去', true);
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => document.querySelector('.garden').dataset.receded === 'false');
    check('取消选中后圆画回来', true);
    await page.close();
  }

  {
    const { page } = await open('skin=crabapple&state=normal&records=16&t=1');
    await page.waitForTimeout(300);
    const plain = await saturationAt(page, 500, 330);
    check('开满后整幅转彩', plain > 25, plain.toFixed(1));
    await page.close();
  }

  {
    // 花开满以后点空白处换成水边那幅，再点换回来；选的那幅下次打开还在
    const { page, errors } = await open('skin=crabapple&state=normal&records=16&t=1');
    await page.waitForFunction(() => document.body.dataset.sceneReady === 'true');
    const bloom = page.locator('.bloom');
    check('开满了可以换画', await bloom.getAttribute('data-swappable') === 'true' && await bloom.getAttribute('data-painting') === 'pink');
    const before = await saturationAt(page, ...BLANK);
    await clickPainting(page, BLANK);
    await page.waitForTimeout(100);
    const water = await saturationAt(page, ...BLANK);
    check('点空白处换成水边那幅', await bloom.getAttribute('data-painting') === 'blue' && before > 25 && water < 0, `换之前 ${before.toFixed(1)}，换之后 ${water.toFixed(1)}`);
    check('换画不打开记录', await page.locator('.inspector').isHidden());
    await clickPainting(page, BLUE_FIRST);
    await page.waitForSelector('.inspector:not([hidden])');
    check('水边那幅里点花打开那条记录', true);
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => document.querySelector('.garden').dataset.receded === 'false');
    await page.reload();
    await page.waitForFunction(() => document.body.dataset.sceneReady === 'true');
    check('下次打开还是水边那幅', await bloom.getAttribute('data-painting') === 'blue' && (await saturationAt(page, ...BLANK)) < 0);
    await clickPainting(page, BLANK);
    await page.waitForTimeout(100);
    check('再点换回粉色', await bloom.getAttribute('data-painting') === 'pink' && (await saturationAt(page, ...BLANK)) > 25);
    check('换画没有报错', errors.length === 0, errors.join(' | '));
    await page.close();
  }

  {
    const { page } = await open('skin=crabapple&state=normal&records=8&t=1');
    await page.waitForFunction(() => document.body.dataset.sceneReady === 'true');
    const before = await saturationAt(page, ...BLANK);
    await clickPainting(page, BLANK);
    await page.waitForTimeout(100);
    const bloom = page.locator('.bloom');
    check('没开满时点空白处不换', await bloom.getAttribute('data-swappable') === 'false' && await bloom.getAttribute('data-painting') === 'pink' && Math.abs(await saturationAt(page, ...BLANK) - before) < 1);
    await page.close();
  }

  {
    const { page, errors } = await open('skin=crabapple&state=normal&records=8&t=1');
    await page.locator('.date-jump').click();
    await page.waitForSelector('.cal-day img');
    check('海棠日历用花做记号', (await page.locator('.cal-day img').first().getAttribute('src')).includes('crabapple/mark-'));
    check('花记号读得到', await page.evaluate(() => {
      const image = document.querySelector('.cal-day img');
      return image.decode().then(() => image.naturalWidth > 0);
    }));
    check('海棠日历四周是印花', (await page.locator('.calendar-pop').evaluate((node) => getComputedStyle(node).backgroundImage)).includes('crabapple/print.webp'));
    check('日期写在题签上', await page.locator('.cal-sheet').evaluate((node) => getComputedStyle(node, '::before').backgroundColor) === 'rgb(252, 248, 239)');
    check('日历的印花读得到', errors.length === 0, errors.join(' | '));
    await page.keyboard.press('Escape');
    await page.close();
  }

  {
    const { page } = await open('state=normal&records=8&day=0');
    check('旧设置里的清晨落到傍晚', await page.evaluate(() => document.documentElement.dataset.period) === 'dusk');
    check('滑块只在傍晚和夜晚之间', await page.locator('.day-slider').getAttribute('min') === '2' && await page.locator('.day-slider').getAttribute('max') === '3');
    await page.click('.settings-button');
    await page.waitForSelector('#fixed-period', { state: 'attached' });
    const options = await page.locator('#fixed-period option').allInnerTexts();
    check('设置里只有傍晚和夜晚', options.join('|') === '跟随系统时间|傍晚|夜晚', options.join('|'));
    await page.close();
  }

  for (const [name, query, text] of [
    ['海棠空状态', 'skin=crabapple&state=empty&t=1', '枝头还很安静'],
    ['星湖空状态', 'state=empty&day=3&t=1', '湖面还很安静'],
  ]) {
    const { page, errors } = await open(query);
    check(name, (await page.locator('.empty-state').innerText()).includes(text) && errors.length === 0, errors.join(' | '));
    await page.close();
  }

  {
    const { page, errors, requested } = await open('state=normal&records=8&day=3');
    check('星湖没有报错', errors.length === 0, errors.join(' | '));
    check('星湖的标题', (await page.locator('.today-title').innerText()).replace(/\s/g, '') === '今天亮起8颗星');
    check('星湖不下载海棠的画', !requested.some((url) => url.includes('assets/crabapple')));
    await page.locator('.date-jump').click();
    await page.waitForSelector('.cal-star');
    check('星湖日历用星做记号', await page.locator('.cal-day img').count() === 0 && await page.locator('.cal-star path').count() >= 1);
    check('星湖日历有星轨和月亮', await page.locator('.sky-ring').count() === 1 && await page.locator('.sky-moon path').count() === 1);
    check('星湖日历是玻璃，没有印花', !(await page.locator('.calendar-pop').evaluate((node) => getComputedStyle(node).backgroundImage)).includes('crabapple/'));
    await page.keyboard.press('Escape');

    // 在设置里换皮肤：页面重开成海棠，再换回来
    await page.locator('.settings-button').click();
    await page.waitForSelector('.skin-picker');
    check('星湖能选时段', await page.locator('#fixed-period').isVisible());
    await page.locator('.skin-card[data-skin-card="crabapple"]').click();
    check('选海棠时收起时段', !(await page.locator('#fixed-period').isVisible()));
    await page.locator('button[data-action="save"]').click();
    await page.waitForURL(/skin=crabapple/);
    await page.waitForFunction(() => document.body.dataset.appReady === 'true');
    check('换到海棠', await page.evaluate(() => document.documentElement.dataset.skin) === 'crabapple' && await page.locator('.bloom').count() === 1);
    await page.locator('.settings-button').click();
    await page.waitForSelector('.skin-picker');
    check('海棠选项是选中的', await page.locator('input[name="skin"][value="crabapple"]').isChecked());
    await page.locator('.skin-card[data-skin-card="starlake"]').click();
    await page.locator('button[data-action="save"]').click();
    await page.waitForURL(/skin=starlake/);
    await page.waitForFunction(() => document.body.dataset.appReady === 'true');
    check('换回星湖', await page.evaluate(() => document.documentElement.dataset.skin) === 'starlake' && await page.locator('.lake').count() === 1);
    await page.close();
  }

  // 换皮肤整页重开，内存里的状态要带过去
  {
    const { page } = await open('state=normal&records=8&day=3');
    await page.locator('#history-search').fill('按钮');
    await page.waitForFunction(() => document.querySelectorAll('.record-item').length > 0);
    await page.waitForTimeout(400);
    await page.locator('.manage-button').click();
    await page.locator('.record-item').first().click();
    await page.locator('.settings-button').click();
    await page.waitForSelector('.skin-picker');
    await page.locator('#quiet-mode').evaluate((node) => { node.click(); });
    await page.locator('.skin-card[data-skin-card="crabapple"]').click();
    await page.locator('button[data-action="save"]').click();
    await page.waitForURL(/skin=crabapple/);
    await page.waitForFunction(() => document.body.dataset.appReady === 'true');
    check('换皮肤后搜索词还在', await page.locator('#history-search').inputValue() === '按钮');
    check('换皮肤后还在批量选择', await page.locator('.journal').getAttribute('data-managing') === 'true');
    check('换皮肤后勾选还在', (await page.locator('.manage-count').innerText()).includes('1 条'), await page.locator('.manage-count').innerText());
    await page.waitForTimeout(300);
    check('海棠的勾选框填上了颜色', await page.locator('.record-item[aria-pressed="true"] .record-check').evaluate((node) => getComputedStyle(node).backgroundColor) === 'rgb(149, 56, 76)');
    check('同一次改的安静模式生效', page.url().includes('quiet=1') && await page.evaluate(() => document.documentElement.dataset.quiet) === 'true', page.url());
    await page.close();
  }

  // 文字保存失败时不能换：设置一提交，原生窗口就换了
  {
    const { page } = await open('state=detail&records=5&day=3');
    await page.locator('.inspector-editor').fill('模拟保存失败');
    await page.locator('.settings-button').click();
    await page.waitForSelector('.skin-picker');
    await page.locator('.skin-card[data-skin-card="crabapple"]').click();
    await page.locator('button[data-action="save"]').click();
    await page.waitForFunction(() => document.querySelector('.settings-saved')?.textContent?.includes('先不换皮肤'));
    check('文字保存失败时不换皮肤', page.url().includes('skin=crabapple') === false && await page.evaluate(() => document.documentElement.dataset.skin) === 'starlake', page.url());
    await page.close();
  }

  // 没写完的文字要先落盘才能换皮肤：换的时候页面整个重开
  {
    const { page } = await open('state=detail&records=5&day=3');
    const saved = [];
    page.on('console', (message) => { if (message.text().startsWith('mock:record.update')) saved.push(message.text()); });
    const editor = page.locator('.inspector-editor');
    await editor.fill('换皮肤之前刚打的字');
    await page.locator('.settings-button').click();
    await page.waitForSelector('.skin-picker');
    await page.locator('.skin-card[data-skin-card="crabapple"]').click();
    await page.locator('button[data-action="save"]').click();
    await page.waitForURL(/skin=crabapple/);
    check('换皮肤时先保存文字', saved.some((line) => line.includes('换皮肤之前刚打的字')), saved.join(' | '));
    await page.close();
  }
} finally {
  await browser.close();
  await server.close();
}
console.log(failed ? `${failed} 项没通过` : '全部通过');
process.exit(failed ? 1 : 0);
