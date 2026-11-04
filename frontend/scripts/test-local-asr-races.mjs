import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { existsSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const frontend = dirname(dirname(fileURLToPath(import.meta.url)));
const server = await createServer({ root: frontend, server: { host: '127.0.0.1', port: 0 }, logLevel: 'error' });
await server.listen();
const port = server.httpServer.address().port;
const executablePath = ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
const browser = await chromium.launch({ headless: true, executablePath });
let failures = 0;

async function fixture() {
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${port}/?state=normal&records=0`);
  await page.waitForFunction(() => document.body.dataset.appReady === 'true');
  await page.evaluate(async () => {
    const { SettingsView } = await import('/src/components/SettingsView.ts');
    const root = document.createElement('div');
    root.id = 'audit-settings';
    root.className = 'settings-view';
    root.style.zIndex = '999';
    document.body.append(root);
    const state = {
      settings: { hotkey: 'Ctrl+Alt+G', speechHotkey: 'Ctrl+Alt+H', dataDirectory: 'C:\\example', freeformFillColor: '#ffffff', deleteAudioAfterSuccess: false, autoStart: false, quietMode: true, autoSpeakAfterSelection: false, speechProvider: 'local', speechModel: 'cloud-model', speechEndpoint: 'wss://example.com/asr', localSpeechEndpoint: 'ws://127.0.0.1:8765/asr', localSpeechModel: 'local-a', speechLanguage: 'zh', skin: 'starlake' },
      tests: [], saves: [], reads: [], switches: [], profileReads: 0, holdSave: false, holdRead: false, modelStatus: null,
      profiles: [
        { id: 'paraformer-streaming-int8', profile: { schemaVersion: 1, id: 'paraformer-streaming-int8', endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-a', expectedModelName: 'Actual CPU' } },
        { id: 'qwen3-asr-0.6b', profile: { schemaVersion: 1, id: 'qwen3-asr-0.6b', endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-b', expectedModelName: 'Actual Qwen' } },
        { id: 'qwen3-asr-1.7b', profile: null },
      ],
    };
    const bridge = {
      subscribe: () => () => {},
      request: async (type, payload) => {
        if (type === 'settings.get') {
          const settings = structuredClone(state.settings);
          if (state.holdRead) await new Promise((resolve) => state.reads.push(resolve));
          return settings;
        }
        if (type === 'audio.devices') return [];
        if (type === 'localAsr.modelInfo') return state.modelStatus;
        if (type === 'localAsr.profiles') { state.profileReads++; return structuredClone(state.profiles); }
        if (type === 'settings.update') {
          state.saves.push(structuredClone(payload));
          if (state.holdSave) await new Promise((resolve) => { state.releaseSave = resolve; });
          state.settings = { ...structuredClone(payload), settingsRevision: (state.settings.settingsRevision ?? 0) + 1 };
          return structuredClone(state.settings);
        }
        if (type === 'connection.test') return await new Promise((resolve, reject) => state.tests.push({ payload, resolve, reject }));
        if (type === 'localAsr.switchProfile') {
          try {
            await new Promise((resolve, reject) => state.switches.push({ payload: structuredClone(payload), resolve, reject }));
            const profile = payload.profile;
            state.settings = { ...state.settings, settingsRevision: (state.settings.settingsRevision ?? 0) + 1, speechProvider: 'local', localSpeechEndpoint: profile.endpoint, localSpeechModel: profile.model };
            state.modelStatus = { endpoint: profile.endpoint, model: profile.model, connected: true, checkedAt: new Date().toISOString(), modelInfo: { name: profile.expectedModelName, catalogId: profile.id } };
            return { settings: structuredClone(state.settings), modelStatus: state.modelStatus, message: '已切换到测试模型' };
          } catch (error) { state.modelStatus = null; throw error; }
        }
        return null;
      },
    };
    window.auditSettings = state;
    state.view = new SettingsView(root, bridge);
    await state.view.show();
  });
  return { page, root: page.locator('#audit-settings') };
}

const cases = [
  ['registered-model-switch-preserves-other-unsaved-edits-and-uses-snapshot', async ({ page, root }) => {
    const before = await page.evaluate(() => structuredClone(window.auditSettings.settings));
    await root.locator('#data-directory').fill('C:\\unsaved-folder');
    await root.locator('[data-model-id="qwen3-asr-0.6b"] [data-action="switch-local-model"]').click();
    await page.waitForFunction(() => window.auditSettings.switches.length === 1);
    const payload = await page.evaluate(() => window.auditSettings.switches[0].payload);
    assert.equal(payload.id, 'qwen3-asr-0.6b');
    assert.equal(payload.profile.model, 'local-b');
    assert.deepEqual(payload.expectedCurrent, before);
    assert.equal(await root.locator('[data-action="save"]').isDisabled(), true);
    assert.equal(await root.locator('[data-action="test"]').isDisabled(), true);
    assert.equal(await root.locator('[data-action="refresh-local-profiles"]').isDisabled(), true);
    assert.equal(await root.locator('#speech-provider').isDisabled(), true);
    await page.evaluate(() => window.auditSettings.switches[0].resolve());
    await page.waitForFunction(() => document.querySelector('#audit-settings #local-speech-model').value === 'local-b');
    assert.equal(await root.locator('#data-directory').inputValue(), 'C:\\unsaved-folder');
    assert.equal(await root.locator('.local-model-option[data-model-id="qwen3-asr-0.6b"]').getAttribute('data-current'), 'true');
    const committed = await page.evaluate(() => structuredClone(window.auditSettings.settings));
    assert.deepEqual(committed, { ...before, settingsRevision: 1, localSpeechModel: 'local-b' });
    assert.equal(await page.evaluate(() => window.auditSettings.saves.length), 0);
    await root.locator('[data-action="save"]').click();
    await page.waitForFunction(() => window.auditSettings.saves.length === 1);
    assert.equal(await page.evaluate(() => window.auditSettings.saves[0].settingsRevision), 1);
    assert.equal(await page.evaluate(() => window.auditSettings.saves[0].dataDirectory), 'C:\\unsaved-folder');
  }],
  ['failed-switch-keeps-previous-choice-and-clears-unverified-identity', async ({ page, root }) => {
    await page.evaluate(async () => {
      window.auditSettings.modelStatus = { endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-a', connected: true, checkedAt: new Date().toISOString(), modelInfo: { name: 'Actual CPU', catalogId: 'paraformer-streaming-int8' } };
      await window.auditSettings.view.show();
    });
    assert.equal(await root.locator('.local-model-option[data-model-id="paraformer-streaming-int8"]').getAttribute('data-current'), 'true');
    await root.locator('[data-model-id="qwen3-asr-0.6b"] [data-action="switch-local-model"]').click();
    await page.waitForFunction(() => window.auditSettings.switches.length === 1);
    await page.evaluate(() => window.auditSettings.switches[0].reject(new Error('未切换，原配置已保留：模型加载失败')));
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="save"]').disabled);
    assert.equal(await root.locator('#local-speech-model').inputValue(), 'local-a');
    assert.match(await root.locator('.settings-saved').innerText(), /原配置已保留/);
    assert.equal(await root.locator('.local-model-option[data-current="true"]').count(), 0);
    assert.equal(await page.evaluate(() => window.auditSettings.settings.localSpeechModel), 'local-a');
    assert.equal(await page.evaluate(() => window.auditSettings.saves.length), 0);
  }],
  ['reopen-waits-for-model-switch-before-reading-committed-choice', async ({ page, root }) => {
    await root.locator('[data-model-id="qwen3-asr-0.6b"] [data-action="switch-local-model"]').click();
    await page.waitForFunction(() => window.auditSettings.switches.length === 1);
    await page.evaluate(() => { window.auditSettings.view.hide(); void window.auditSettings.view.show(); });
    assert.equal(await root.locator('[data-action="save"]').isDisabled(), true);
    await page.evaluate(() => window.auditSettings.switches[0].resolve());
    await page.waitForFunction(() => document.querySelector('#audit-settings #local-speech-model')?.value === 'local-b');
    assert.equal(await root.locator('.local-model-option[data-model-id="qwen3-asr-0.6b"]').getAttribute('data-current'), 'true');
    assert.equal(await page.evaluate(() => window.auditSettings.switches.length), 1);
  }],
  ['refresh-discovers-registration-without-loading-saving-or-switching', async ({ page, root }) => {
    const target = root.locator('.local-model-option[data-model-id="qwen3-asr-1.7b"]');
    assert.equal(await target.locator('[data-action="switch-local-model"]').isDisabled(), true);
    await page.evaluate(() => {
      window.auditSettings.profiles[2].profile = { schemaVersion: 1, id: 'qwen3-asr-1.7b', endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-c', expectedModelName: 'Actual bigger Qwen' };
    });
    await root.locator('[data-action="refresh-local-profiles"]').click();
    await page.waitForFunction(() => !document.querySelector('#audit-settings .local-model-option[data-model-id="qwen3-asr-1.7b"] [data-action="switch-local-model"]').disabled);
    assert.match(await target.innerText(), /已登记 · 切换时验证/);
    assert.deepEqual(await page.evaluate(() => [window.auditSettings.tests.length, window.auditSettings.switches.length, window.auditSettings.saves.length]), [0, 0, 0]);
  }],
  ['same-route-with-mismatched-identity-remains-verifiable-not-current', async ({ page, root }) => {
    await page.evaluate(async () => {
      window.auditSettings.modelStatus = { endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-a', connected: true, checkedAt: new Date().toISOString(), modelInfo: { name: 'Wrong family', catalogId: 'qwen3-asr-1.7b' } };
      await window.auditSettings.view.show();
    });
    const target = root.locator('.local-model-option[data-model-id="paraformer-streaming-int8"]');
    assert.equal(await target.getAttribute('data-current'), 'false');
    assert.equal(await target.locator('[data-action="switch-local-model"]').isDisabled(), false);
    assert.match(await target.innerText(), /身份待验证/);
  }],
  ['normal-connection-test-updates-current-card-only-after-identity-match', async ({ page, root }) => {
    await root.locator('[data-action="test-local-model"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    await page.evaluate(() => window.auditSettings.tests[0].resolve({ ok: true, message: 'verified', modelStatus: { endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-a', connected: true, checkedAt: new Date().toISOString(), modelInfo: { name: 'Actual CPU', catalogId: 'paraformer-streaming-int8' } } }));
    await page.waitForFunction(() => document.querySelector('#audit-settings .local-model-option[data-model-id="paraformer-streaming-int8"]').dataset.current === 'true');
  }],
  ['opening-settings-does-not-query-or-load-a-model', async ({ page, root }) => {
    assert.equal(await page.evaluate(() => window.auditSettings.tests.length), 0);
    assert.equal(await root.locator('#local-model-info').getAttribute('data-known'), 'false');
  }],
  ['late-model-info-does-not-label-new-configuration', async ({ page, root }) => {
    await root.locator('[data-action="test-local-model"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    assert.equal(await root.locator('[data-action="test"]').isDisabled(), true);
    await root.locator('#local-speech-model').fill('local-b');
    await page.evaluate(() => window.auditSettings.tests[0].resolve({ ok: true, message: 'old success', modelStatus: { endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-a', connected: true, checkedAt: new Date().toISOString(), modelInfo: { name: 'wrong old model' } } }));
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="test"]').disabled);
    assert.equal(await root.locator('#local-model-info').getAttribute('data-known'), 'false');
    assert.doesNotMatch(await root.locator('#local-model-info').innerText(), /wrong old model/);
  }],
  ['legacy-service-and-unknown-memory-are-not-fabricated', async ({ page, root }) => {
    await root.locator('[data-action="test"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    await page.evaluate(() => window.auditSettings.tests[0].resolve({ ok: true, message: 'legacy' }));
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="test"]').disabled);
    assert.match(await root.locator('#local-model-info').innerText(), /服务已连接，尚未报告模型/);
    await root.locator('[data-action="test-local-model"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 2);
    await page.evaluate(() => window.auditSettings.tests[1].resolve({ ok: true, message: 'new service', modelStatus: { endpoint: 'ws://127.0.0.1:8765/asr', model: 'local-a', connected: true, checkedAt: new Date().toISOString(), modelInfo: { name: '<img src=x onerror=alert(1)>', device: 'cuda:0' } } }));
    await page.waitForFunction(() => document.querySelector('#audit-settings #local-model-info').dataset.known === 'true');
    assert.equal(await root.locator('#local-model-info img').count(), 0);
    assert.match(await root.locator('#local-model-info').innerText(), /未提供/);
    assert.doesNotMatch(await root.locator('#local-model-info').innerText(), /0 GB/);
    await root.locator('[data-action="test"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 3);
    await page.evaluate(() => window.auditSettings.tests[2].reject(new Error('service stopped')));
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="test"]').disabled);
    assert.equal(await root.locator('#local-model-info').getAttribute('data-known'), 'false');
    assert.match(await root.locator('#local-model-info').innerText(), /上次连接未成功/);
  }],
  ['reopen-does-not-start-a-second-model-test', async ({ page, root }) => {
    await root.locator('[data-action="test"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    await root.locator('[data-action="back"]').click();
    await page.evaluate(() => window.auditSettings.view.show());
    assert.equal(await root.locator('[data-action="test"]').isDisabled(), true, 'reopened dialog enables duplicate connection test');
    await page.evaluate(() => window.auditSettings.tests[0].resolve({ ok: true, message: 'old success' }));
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="test"]').disabled);
  }],
  ['old-error-does-not-label-new-configuration-as-broken', async ({ page, root }) => {
    await root.locator('[data-action="test"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    await root.locator('#local-speech-model').fill('local-b');
    await page.evaluate(() => window.auditSettings.tests[0].reject(new Error('old model failed')));
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="test"]').disabled);
    assert.notEqual(await root.locator('#connection-result').getAttribute('data-state'), 'error');
    assert.match(await root.locator('#connection-result').innerText(), /重新测试/);
  }],
  ['test-carries-the-saved-configuration-snapshot', async ({ page, root }) => {
    await root.locator('[data-action="test"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    const payload = await page.evaluate(() => window.auditSettings.tests[0].payload);
    assert.equal(payload?.settings?.speechProvider, 'local', 'test uses mutable global settings instead of its snapshot');
    assert.equal(payload.settings.localSpeechModel, 'local-a');
    await page.evaluate(() => window.auditSettings.tests[0].resolve({ ok: true, message: 'ok' }));
  }],
  ['slow-save-cannot-race-a-second-save', async ({ page, root }) => {
    await page.evaluate(() => { window.auditSettings.holdSave = true; });
    await root.locator('[data-action="save"]').click();
    await page.waitForFunction(() => window.auditSettings.saves.length === 1);
    assert.equal(await root.locator('[data-action="save"]').isDisabled(), true, 'second save can overtake first');
    await page.evaluate(() => window.auditSettings.releaseSave());
    await page.waitForFunction(() => !document.querySelector('#audit-settings [data-action="save"]').disabled);
  }],
  ['reopen-waits-for-pending-save-before-reading-settings', async ({ page, root }) => {
    await page.evaluate(() => { window.auditSettings.holdSave = true; });
    await root.locator('#local-speech-model').fill('saved-after-reopen');
    await root.locator('[data-action="save"]').click();
    await page.waitForFunction(() => window.auditSettings.saves.length === 1);
    await page.evaluate(() => { window.auditSettings.view.hide(); void window.auditSettings.view.show(); });
    assert.equal(await root.locator('[data-action="save"]').isDisabled(), true);
    await page.evaluate(() => window.auditSettings.releaseSave());
    await page.waitForFunction(() => document.querySelector('#audit-settings #local-speech-model')?.value === 'saved-after-reopen');
  }],
  ['cloud-key-save-still-tests-the-saved-cloud-configuration', async ({ page, root }) => {
    await root.locator('#speech-provider').selectOption('dashscope');
    await root.locator('#api-key').fill('synthetic-test-key');
    await root.locator('[data-action="test"]').click();
    await page.waitForFunction(() => window.auditSettings.tests.length === 1);
    const payload = await page.evaluate(() => window.auditSettings.tests[0].payload);
    assert.equal(payload.settings.speechProvider, 'dashscope');
    assert.equal(await root.locator('#api-key').inputValue(), '');
    await page.evaluate(() => window.auditSettings.tests[0].resolve({ ok: true, message: 'cloud-ok' }));
    await page.waitForFunction(() => document.querySelector('#audit-settings #connection-result')?.textContent === 'cloud-ok');
  }],
  ['late-settings-read-cannot-replace-a-newer-dialog', async ({ page, root }) => {
    await page.evaluate(() => { window.auditSettings.holdRead = true; void window.auditSettings.view.show(); });
    await page.waitForFunction(() => window.auditSettings.reads.length === 1);
    await page.evaluate(async () => {
      window.auditSettings.view.hide();
      window.auditSettings.holdRead = false;
      window.auditSettings.settings.localSpeechModel = 'newly-saved-model';
      await window.auditSettings.view.show();
      window.auditSettings.reads[0]();
    });
    await page.waitForTimeout(30);
    assert.equal(await root.locator('#local-speech-model').inputValue(), 'newly-saved-model');
  }],
];
try {
  for (const [name, run] of cases) {
    const context = await fixture();
    try { await run(context); console.log(`PASS ${name}`); }
    catch (error) { failures++; console.error(`FAIL ${name}: ${error.message}`); }
    finally { await context.page.close(); }
  }
} finally { await browser.close(); await server.close(); }
console.log(`Cases: ${cases.length}; failures: ${failures}`);
process.exitCode = failures ? 1 : 0;
