// Real App/StageView + MockBridge, with controllable transport delays. No native host.
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 5193, strictPort: true }, logLevel: 'error' });
await server.listen();
let browser;
let failures = 0;
const skin = process.env.QS_SKIN ?? 'starlake';
const filter = process.argv.slice(2);
try {
  browser = await chromium.launch({ headless: true, executablePath: ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe'].find(existsSync) });
  async function run(name, test) {
    if (filter.length && !filter.includes(name)) return;
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    // Block non-loopback traffic, including accidental requests added later.
    await page.route('**/*', route => {
      const url = new URL(route.request().url());
      if (url.hostname !== '127.0.0.1') return route.abort();
      if (url.pathname === '/race-test.html') return route.fulfill({ contentType: 'text/html', body: '<html><head><link rel="stylesheet" href="/src/styles.css"></head><body><div id="app"></div></body></html>' });
      return route.continue();
    });
    try {
      await page.goto(`http://127.0.0.1:5193/race-test.html?skin=${skin}&day=3&quiet=1&webgl=0`);
      await page.evaluate(async () => {
        const { MockBridge } = await import('/src/bridge/mockBridge.ts');
        const { App } = await import('/src/app/App.ts');
        const { readQuery } = await import('/src/app/query.ts');
        const mock = new MockBridge(8);
        const listeners = new Map();
        const state = window.transport = { calls: [], pending: [], holdUpdate: false, failUpdate: false, holdRetry: false, holdDelete: false, heldQuery: null };
        const wait = (type, payload, result) => new Promise((resolve, reject) => state.pending.push({ type, payload, resolve: () => resolve(result), reject }));
        const bridge = {
          async request(type, payload) {
            const call = { type, payload, done: false };
            state.calls.push(call);
            if (type === 'record.update') {
              if (state.holdUpdate) await wait(type, payload);
              if (state.failUpdate) throw new Error('synthetic write failure');
            }
            if (type === 'record.retry' && state.holdRetry) await wait(type, payload);
            if (type === 'record.delete' && state.holdDelete) await wait(type, payload);
            let result = await mock.request(type, payload);
            if (type === 'records.query') {
              result = structuredClone(result).map(record => record.status === 'incomplete' ? { ...record, audioPath: 'synthetic.wav' } : record);
              if (state.heldQuery === payload.query) result = await wait(type, payload, result);
            }
            call.done = true;
            return result;
          },
          subscribe(type, listener) {
            const callbacks = listeners.get(type) ?? new Set();
            callbacks.add(listener);
            listeners.set(type, callbacks);
            return () => callbacks.delete(listener);
          },
        };
        window.emit = (type, payload = {}) => listeners.get(type)?.forEach(listener => listener(payload));
        window.release = type => {
          const index = state.pending.findIndex(item => item.type === type);
          if (index < 0) throw new Error(`No pending ${type}`);
          state.pending.splice(index, 1)[0].resolve();
        };
        const options = readQuery(new URLSearchParams(location.search));
        document.documentElement.dataset.skin = options.skin;
        await new App(document.querySelector('#app'), bridge, options).initialize();
      });
      await test(page);
      assert.deepEqual(errors, [], 'Unhandled page errors');
      console.log(`PASS ${name}`);
    } catch (error) {
      failures++;
      console.log(`FAIL ${name}: ${error.name}: ${error.message}`);
    } finally { await page.close(); }
  }

  const item = (page, index) => page.locator('.record-item').nth(index);
  const editor = page => page.locator('.inspector-editor');
  const pending = (page, type) => page.waitForFunction(type => window.transport.pending.some(item => item.type === type), type);
  const called = (page, type) => page.evaluate(type => window.transport.calls.filter(call => call.type === type).length, type);

  await run('batch-delete-waits-for-draft', async page => {
    await page.evaluate(() => { window.transport.holdUpdate = true; });
    await item(page, 0).click();
    await editor(page).fill('synthetic draft before delete');
    await page.locator('.manage-button').click();
    await pending(page, 'record.update');
    await item(page, 0).click();
    await page.locator('.manage-delete').click();
    await page.locator('.manage-delete').click();
    assert.equal(await called(page, 'records.delete'), 0, 'Deletion started before the in-flight draft was saved');
    await page.evaluate(() => { window.transport.holdUpdate = false; window.release('record.update'); });
    await page.waitForFunction(() => window.transport.calls.some(call => call.type === 'records.delete' && call.done));
    await page.locator('.toast button').click();
    await page.waitForFunction(() => document.querySelector('.record-item .record-text')?.textContent === 'synthetic draft before delete');
    assert.equal(await item(page, 0).locator('.record-text').innerText(), 'synthetic draft before delete', 'Undo lost the draft');
  });

  await run('batch-delete-keeps-failed-draft', async page => {
    await page.evaluate(() => { window.transport.failUpdate = true; });
    await item(page, 0).click();
    await editor(page).fill('synthetic failed draft');
    await page.locator('.manage-button').click();
    await item(page, 0).click();
    await page.locator('.manage-delete').click();
    await page.locator('.manage-delete').click();
    assert.equal(await called(page, 'records.delete'), 0, 'Deletion discarded an unsaved draft after save failure');
    await page.locator('.manage-cancel').click();
    await item(page, 0).click();
    assert.equal(await editor(page).inputValue(), 'synthetic failed draft');
  });

  await run('retry-cannot-be-bypassed-by-selection', async page => {
    await page.evaluate(() => { window.transport.holdRetry = true; });
    await item(page, 3).click();
    await page.locator('[data-action="retry"]').click();
    await pending(page, 'record.retry');
    await item(page, 0).click();
    assert.equal(await editor(page).getAttribute('readonly'), null, 'Unrelated record was locked');
    await item(page, 3).click();
    assert.equal(await editor(page).evaluate(node => node.readOnly), true, 'Reopening the same record bypassed the retry lock');
    assert.equal(await page.locator('[data-action="retry"]').isDisabled(), true, 'A second retry was allowed');
    await page.evaluate(() => { window.transport.holdRetry = false; window.release('record.retry'); });
    await page.waitForFunction(() => !document.querySelector('.inspector-editor').readOnly);
    assert.equal(await called(page, 'record.retry'), 1);
  });

  await run('retry-save-must-not-replace-new-selection', async page => {
    await page.evaluate(() => { window.transport.holdUpdate = true; window.transport.holdRetry = true; });
    await item(page, 3).click();
    await editor(page).fill('synthetic A draft');
    await page.locator('[data-action="retry"]').click();
    await pending(page, 'record.update');
    await item(page, 0).click();
    const selectedText = await editor(page).inputValue();
    await page.evaluate(() => { window.transport.holdUpdate = false; window.release('record.update'); });
    await pending(page, 'record.retry');
    assert.equal(await editor(page).evaluate(node => node.readOnly), false, 'Saving A then switching to B locked B');
    await page.evaluate(() => { window.transport.holdRetry = false; window.release('record.retry'); });
    await page.waitForFunction(() => window.transport.calls.some(call => call.type === 'record.retry' && call.done));
    assert.equal(await editor(page).inputValue(), selectedText, 'Retry completion replaced B with A');
  });

  await run('delete-result-keeps-new-selection', async page => {
    await page.evaluate(() => { window.transport.holdDelete = true; });
    await item(page, 0).click();
    await page.locator('[data-action="delete"]').click();
    await page.locator('[data-action="delete"]').click();
    await pending(page, 'record.delete');
    await item(page, 1).click();
    const selectedText = await editor(page).inputValue();
    await page.evaluate(() => window.release('record.delete'));
    await page.waitForFunction(() => !!document.querySelector('.toast button'));
    assert.equal(await page.locator('.inspector').isVisible(), true, 'Deleting A closed the newly selected B');
    assert.equal(await editor(page).inputValue(), selectedText, 'Deleting A changed B');
    assert.equal(await page.locator('.selection-summary').innerText(), '已选 1 条');
  });

  await run('latest-search-wins', async page => {
    await page.evaluate(() => { window.transport.heldQuery = 'Figma'; });
    await page.locator('#history-search').fill('Figma');
    await pending(page, 'records.query');
    await page.locator('#history-search').fill('Microsoft Edge');
    await page.waitForFunction(() => window.transport.calls.some(call => call.type === 'records.query' && call.payload.query === 'Microsoft Edge' && call.done));
    const latest = await page.locator('.record-item').allTextContents();
    assert.ok(latest.length > 0 && latest.every(text => text.includes('Microsoft Edge')));
    await page.evaluate(() => window.release('records.query'));
    assert.deepEqual(await page.locator('.record-item').allTextContents(), latest, 'Older search overwrote the current query');
  });

  await run('old-query-cannot-revert-saved-text', async page => {
    await item(page, 0).click();
    await page.evaluate(() => { window.transport.heldQuery = ''; window.emit('records.changed'); });
    await pending(page, 'records.query');
    await editor(page).fill('synthetic newest saved text');
    await page.locator('.close-button').click();
    await page.waitForFunction(() => window.transport.calls.some(call => call.type === 'window.close' && call.done));
    await page.evaluate(() => { window.transport.heldQuery = null; window.release('records.query'); });
    await page.waitForFunction(() => window.transport.calls.filter(call => call.type === 'records.query').every(call => call.done));
    assert.equal(await item(page, 0).locator('.record-text').innerText(), 'synthetic newest saved text', 'Old query reverted the saved text in the list');
    await item(page, 1).click();
    await item(page, 0).click();
    assert.equal(await editor(page).inputValue(), 'synthetic newest saved text');
  });
  // Tray "设置" while the window is already open: native no longer reloads the page, it asks the page to switch.
  await run('view-open-settings-keeps-draft', async page => {
    await page.evaluate(() => { window.transport.holdUpdate = true; });
    await item(page, 0).click();
    await editor(page).fill('synthetic draft before tray settings');
    await page.evaluate(() => { window.pageAlive = true; window.emit('view.open', { state: 'settings' }); });
    await page.waitForFunction(() => !document.querySelector('.settings-view')?.hidden, null, { timeout: 2000 }).catch(() => {});
    assert.equal(await page.locator('.settings-view').isVisible(), true, 'view.open did not open settings on the live page');
    assert.equal(await page.evaluate(() => window.pageAlive === true), true, 'The page was reloaded');
    await pending(page, 'record.update');
    await page.evaluate(() => { window.transport.holdUpdate = false; window.release('record.update'); });
    await page.waitForFunction(() => window.transport.calls.some(call => call.type === 'record.update' && call.payload.text === 'synthetic draft before tray settings' && call.done));
  });
} finally {
  await browser?.close();
  await server.close();
}
console.log(`Failures: ${failures}`);
process.exitCode = failures ? 1 : 0;
