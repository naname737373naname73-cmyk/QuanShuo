import { clampDay } from '../app/query';
import { SKIN_TEXT, type Skin } from '../app/skin';
import type { AppSettings, Bridge, LocalAsrProfileEntry, LocalModelStatus } from '../bridge/types';
import { localModelRecommendations, renderLocalModel } from './LocalModelPanel';
import type { MicrophoneDevice } from './settingsTypes';
import { element, escapeHtml } from '../utils/html';
import { icon } from './icons';

const KNOWN_MODELS = [
  'qwen-audio-3.1-asr-flash-message',
  'qwen-audio-3.1-asr-flash',
  'qwen-audio-3.0-asr-flash',
  'paraformer-realtime-v2',
  'fun-asr-realtime',
];
const MODEL_NOTES: Record<string, string> = {
  'qwen-audio-3.1-asr-flash-message': '推荐。边说边出字。',
  'qwen-audio-3.1-asr-flash': '说完再出字，不能边说边看。超过 5 分钟的录音自动改用推荐模型。',
  'qwen-audio-3.0-asr-flash': '说完再出字，不能边说边看。超过 5 分钟的录音自动改用推荐模型。',
  'paraformer-realtime-v2': '上一代实时模型，边说边出字。',
  'fun-asr-realtime': 'Fun-ASR 实时模型，边说边出字。',
  __custom: '填写你在阿里云百炼已开通的模型名。',
};
// 这类模型只接受公网文件地址，圈说的录音在本机，所以列出来但不能选。
const URL_ONLY_MODEL = 'qwen-audio-3.1-asr-flash-filetrans';


const SKINS: Array<[Skin, string, string]> = [
  ['starlake', '星湖', '夜里看湖，随时间变化'],
  ['crabapple', '海棠', '手绘花册，每天重新开花'],
];
/** 设置：从右侧滑出的长卷。 */
export class SettingsView extends EventTarget {
  private settings: AppSettings | null = null;
  private content: HTMLElement | null = null;
  private savedNote: HTMLElement | null = null;
  private viewRevision = 0;
  private saving = false;
  private testing = false;
  private localModelStatus: LocalModelStatus | null = null;
  private localProfiles: LocalAsrProfileEntry[] | null = null;
  private switchingModel = false;
  private refreshingProfiles = false;
  private saveFinished: Promise<void> = Promise.resolve();
  /** 换皮肤前的检查。原生层一收到设置就换了，所以文字没保存好时不能提交。 */
  beforeSkinChange: () => Promise<boolean> = () => Promise.resolve(true);

  constructor(private root: HTMLElement, private bridge: Bridge) {
    super();
    root.addEventListener('click', (event) => {
      const target = event.target as HTMLElement;
      if (target.classList.contains('settings-scrim')) this.hide();
      const action = target.closest<HTMLButtonElement>('button[data-action]')?.dataset.action;
      if (action === 'back') this.hide();
      if (action === 'save') void this.save();
      if (action === 'test' || action === 'test-local-model') void this.testConnection();
      if (action === 'copy-local-assessment') void this.copyLocalPrompt('assessment');
      if (action === 'copy-local-prompt') void this.copyLocalPrompt('setup');
      const modelId = target.closest<HTMLButtonElement>('button')?.dataset.modelId;
      if (action === 'copy-model-assessment') void this.copyLocalPrompt('assessment', modelId);
      if (action === 'copy-model-setup') void this.copyLocalPrompt('setup', modelId);
      if (action === 'refresh-local-profiles') void this.refreshLocalProfiles();
      if (action === 'switch-local-model' && modelId) void this.switchLocalModel(modelId);
      if (action === 'local-model-docs') void this.openLocalModelDocs(target.closest<HTMLButtonElement>('button')?.dataset.model ?? '');
      if (action === 'microphone-permission') void this.bridge.request('system.microphoneSettings');
      if (action === 'clear-key') void this.clearKey();
    });
  }

  async show(): Promise<void> {
    const revision = ++this.viewRevision;
    this.settings = null;
    this.root.hidden = false;
    this.root.replaceChildren();
    const scrim = element('div', 'settings-scrim');
    const sheet = element('section', 'settings-sheet glass');
    sheet.setAttribute('aria-label', '设置');
    const head = element('header', 'settings-head');
    const close = this.button('', 'back');
    close.className = 'icon-button';
    close.innerHTML = icon('close');
    close.setAttribute('aria-label', '关闭设置');
    head.append(element('h2', undefined, '设置'), close);
    this.content = element('div', 'settings-content', '正在读取');
    const foot = element('footer', 'settings-foot');
    this.savedNote = element('span', 'settings-saved');
    foot.append(this.savedNote, this.button('保存', 'save', true));
    sheet.append(head, this.content, foot);
    this.root.append(scrim, sheet);
    this.syncBusy();
    try {
      // 重新打开时等正在保存的设置落盘，避免读回保存前的旧值。
      await this.saveFinished;
      if (!this.isCurrentView(revision)) return;
      const [settings, devices, modelStatus, profiles] = await Promise.all([
        this.bridge.request<AppSettings>('settings.get'),
        this.bridge.request<MicrophoneDevice[]>('audio.devices').catch(() => []),
        this.bridge.request<LocalModelStatus | null>('localAsr.modelInfo').catch(() => null),
        this.bridge.request<LocalAsrProfileEntry[]>('localAsr.profiles').catch(() => null),
      ]);
      if (!this.isCurrentView(revision)) return;
      this.settings = settings;
      this.localModelStatus = modelStatus;
      this.localProfiles = profiles;
      this.render(devices);
      this.syncBusy();
    } catch (error) {
      if (this.isCurrentView(revision) && this.content) this.content.textContent = `读取设置失败：${String(error)}`;
    }
  }

  hide(): void {
    this.viewRevision++;
    this.root.hidden = true;
    this.dispatchEvent(new Event('closed'));
  }

  private render(devices: MicrophoneDevice[]): void {
    if (!this.content || !this.settings) return;
    const settings = this.settings;
    this.content.replaceChildren();

    const capture = this.section('圈选');
    capture.append(
      this.field('圈选快捷键', '<input class="text-input mono" id="hotkey" readonly>', '点一下输入框，然后按下新的组合键。被其他程序占用时会提示。'),
      this.field('说话快捷键', '<input class="text-input mono" id="speech-hotkey" readonly>', '不用截图，直接说。再按一次结束，把文字贴回刚才的窗口。'),
      this.toggle('选完自动开始说话', 'auto-speak', settings.autoSpeakAfterSelection),
      this.field('自由圈选外的填充色', '<input class="text-input mono" id="fill-color" type="color">'),
    );

    const speech = this.section('语音识别');
    const cloud = element('div');
    cloud.id = 'cloud-speech-fields';
    const local = element('div');
    local.id = 'local-speech-fields';
    const modelPanel = element('div', 'local-model-panel');
    modelPanel.id = 'local-model-info';
    modelPanel.setAttribute('aria-live', 'polite');
    const promptButtons = element('div', 'button-row local-prompt-buttons');
    promptButtons.append(this.button('① 复制检测提示词', 'copy-local-assessment'), this.button('② 复制安装提示词', 'copy-local-prompt'));
    local.append(
      modelPanel,
      localModelRecommendations(this.localProfiles, settings, this.localModelStatus),
      this.note('不知道选哪个？用下面的通用提示词，让 AI 检查电脑并选一个合适的。想安装时，再把第二步发给它。'),
      promptButtons,
      this.field('本地服务地址', '<input class="text-input mono" id="local-speech-endpoint" placeholder="ws://127.0.0.1:8765/asr">', '服务需按安装提示词接入圈说；仅连接本机，不会自动转到云端。'),
      this.field('服务配置 ID', '<input class="text-input mono" id="local-speech-model" placeholder="default">', '由安装 agent 填写；default 表示服务默认配置。实际模型以上方服务返回的资料为准，改这里不会安装模型。'),
    );
    speech.append(
      this.field('识别来源', '<select class="select-input" id="speech-provider"><option value="dashscope">阿里云</option><option value="local">本地服务</option></select>'),
      local, cloud,
    );
    cloud.append(
      this.field('阿里云 API Key', '<input class="text-input" id="api-key" type="password" autocomplete="off" placeholder="留空表示不修改已保存的 Key">', '用 Windows DPAPI 加密保存，不写入设置文件。'),
      this.field('模型', `<select class="select-input" id="speech-model">${KNOWN_MODELS.map((model) => `<option value="${model}">${model}</option>`).join('')}<option disabled>${URL_ONLY_MODEL}（只收公网地址，本地录音用不了）</option><option value="__custom">自定义</option></select><input class="text-input mono" id="speech-model-custom" placeholder="填写模型名" hidden><small id="model-desc"></small>`),
      this.field('热词表 ID', '<input class="text-input" id="vocabulary-id" placeholder="可选">'),
      this.field('识别地址', '<input class="text-input mono" id="speech-endpoint">', '高级选项，业务空间可填写专属 wss 地址。'),
    );
    speech.append(
      this.field('语言', '<select class="select-input" id="speech-language"><option value="zh">中文（夹英文术语）</option><option value="en">English</option><option value="ja">日本語</option><option value="ko">한국어</option></select>'),
      this.field('麦克风', `<select class="select-input" id="microphone"><option value="">系统默认</option>${devices.map((device) => `<option value="${device.index}">${escapeHtml(device.name)}</option>`).join('')}</select>`),
    );
    const buttons = element('div', 'button-row');
    buttons.append(this.button('测试连接', 'test'), this.button('打开麦克风权限', 'microphone-permission'));
    const result = element('div', 'connection-result', '测试会打开麦克风约 0.25 秒，再向阿里云确认 Key 和模型可用。');
    result.id = 'connection-result';
    const clearKey = this.button('清除已保存的 API Key', 'clear-key');
    clearKey.className = 'link-button';
    cloud.append(clearKey);
    speech.append(buttons, result);

    const storage = this.section('保存');
    storage.append(
      this.field('数据目录', '<input class="text-input mono" id="data-directory">', '重启圈说后生效，旧目录的记录不会被移动。'),
      this.toggle('转写成功后删除录音', 'delete-audio', settings.deleteAudioAfterSuccess),
    );

    const general = this.section('外观与系统');
    general.append(
      this.skinPicker(settings.skin ?? 'starlake'),
      this.field('场景时段', '<select class="select-input" id="fixed-period"><option value="">跟随系统时间</option><option value="2">傍晚</option><option value="3">夜晚</option></select>'),
      this.toggle('安静模式', 'quiet-mode', settings.quietMode),
      this.note(SKIN_TEXT[settings.skin ?? 'starlake'].quietNote),
      this.toggle('开机自启', 'auto-start', settings.autoStart),
    );

    this.content.append(speech, capture, storage, general);
    this.value('speech-provider', settings.speechProvider ?? 'dashscope');
    this.value('local-speech-endpoint', settings.localSpeechEndpoint ?? 'ws://127.0.0.1:8765/asr');
    this.value('local-speech-model', settings.localSpeechModel ?? 'default');
    for (const id of ['local-speech-endpoint', 'local-speech-model']) this.input(id).addEventListener('input', () => this.syncLocalModel());
    this.input('speech-provider').addEventListener('change', () => this.syncProvider());
    this.syncProvider();
    this.value('speech-endpoint', settings.speechEndpoint);
    this.value('speech-language', settings.speechLanguage);
    this.value('microphone', settings.microphoneDeviceId ?? '');
    this.value('vocabulary-id', settings.vocabularyId ?? '');
    this.value('hotkey', settings.hotkey);
    this.value('speech-hotkey', settings.speechHotkey);
    this.value('data-directory', settings.dataDirectory);
    this.value('fill-color', settings.freeformFillColor);
    // 以前存的清晨和正午已经没有了，显示成傍晚
    this.value('fixed-period', settings.fixedDayPeriod === undefined || settings.fixedDayPeriod === null ? '' : String(clampDay(settings.fixedDayPeriod)));
    this.bindHotkey('hotkey');
    this.bindHotkey('speech-hotkey');
    this.bindModel(settings);
    this.syncSkin();
  }

  private skinPicker(current: Skin): HTMLElement {
    const picker = element('fieldset', 'skin-picker');
    picker.innerHTML = `<legend>皮肤</legend>${SKINS.map(([value, name, line]) => `<label class="skin-card" data-skin-card="${value}"><input type="radio" name="skin" value="${value}" ${value === current ? 'checked' : ''}><span class="skin-swatch" aria-hidden="true"></span><span class="skin-name">${name}</span><span class="skin-line">${line}</span></label>`).join('')}`;
    picker.addEventListener('change', () => this.syncSkin());
    return picker;
  }

  private chosenSkin(): Skin {
    return this.root.querySelector<HTMLInputElement>('input[name="skin"]:checked')?.value === 'crabapple' ? 'crabapple' : 'starlake';
  }

  /** 海棠不随时间变化，时段选项收起来。值留在表单里，切回星湖时还在。 */
  private syncSkin(): void {
    this.input('fixed-period').closest<HTMLElement>('.field')!.hidden = this.chosenSkin() === 'crabapple';
  }

  private bindHotkey(id: string): void {
    const input = this.input(id);
    input.addEventListener('keydown', (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (['Control', 'Alt', 'Shift', 'Meta'].includes(event.key)) return;
      const parts: string[] = [];
      if (event.ctrlKey) parts.push('Ctrl');
      if (event.altKey) parts.push('Alt');
      if (event.shiftKey) parts.push('Shift');
      if (event.metaKey) parts.push('Win');
      if (parts.length === 0) return;
      const key = event.code.startsWith('Key') ? event.code.slice(3) : event.code.startsWith('Digit') ? event.code.slice(5) : event.key.length === 1 ? event.key.toUpperCase() : event.key;
      input.value = [...parts, key].join('+');
    });
  }

  private bindModel(settings: AppSettings): void {
    const select = this.input<HTMLSelectElement>('speech-model');
    const custom = this.input('speech-model-custom');
    const description = this.root.querySelector('#model-desc')!;
    if (KNOWN_MODELS.includes(settings.speechModel)) select.value = settings.speechModel;
    else {
      select.value = '__custom';
      custom.value = settings.speechModel;
    }
    const sync = () => {
      custom.hidden = select.value !== '__custom';
      description.textContent = MODEL_NOTES[select.value] ?? '';
    };
    select.addEventListener('change', sync);
    sync();
  }

  private async save(): Promise<AppSettings | null> {
    if (!this.settings || this.saving || this.switchingModel) return null;
    const revision = this.viewRevision;
    const previousSkin = this.settings.skin ?? 'starlake';
    const apiKey = this.input('api-key').value;
    const select = this.input<HTMLSelectElement>('speech-model');
    const settings: AppSettings = {
      ...this.settings,
      hotkey: this.input('hotkey').value || 'Ctrl+Alt+G',
      speechHotkey: this.input('speech-hotkey').value || 'Ctrl+Alt+H',
      dataDirectory: this.input('data-directory').value,
      freeformFillColor: this.input('fill-color').value,
      autoSpeakAfterSelection: this.input('auto-speak').checked,
      deleteAudioAfterSuccess: this.input('delete-audio').checked,
      autoStart: this.input('auto-start').checked,
      quietMode: this.input('quiet-mode').checked,
      speechModel: select.value === '__custom' ? this.input('speech-model-custom').value.trim() : select.value,
      speechProvider: this.input('speech-provider').value === 'local' ? 'local' : 'dashscope',
      localSpeechEndpoint: this.input('local-speech-endpoint').value.trim(),
      localSpeechModel: this.input('local-speech-model').value.trim(),
      speechEndpoint: this.input('speech-endpoint').value,
      speechLanguage: this.input<HTMLSelectElement>('speech-language').value,
      microphoneDeviceId: this.input<HTMLSelectElement>('microphone').value || undefined,
      vocabularyId: this.input('vocabulary-id').value || undefined,
      fixedDayPeriod: this.input<HTMLSelectElement>('fixed-period').value ? Number(this.input<HTMLSelectElement>('fixed-period').value) : undefined,
      skin: this.chosenSkin(),
    };
    this.saving = true;
    let releaseSave!: () => void;
    this.saveFinished = new Promise<void>((resolve) => { releaseSave = resolve; });
    this.syncBusy();
    try {
      if (settings.skin !== previousSkin && !(await this.beforeSkinChange())) {
        if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = '还有文字没保存成功，先不换皮肤。';
        return null;
      }
      if (apiKey && settings.speechProvider !== 'local') await this.bridge.request('secret.set', { apiKey });
      const saved = await this.bridge.request<AppSettings>('settings.update', settings);
      if (this.isCurrentView(revision)) {
        this.settings = saved;
        this.syncLocalProfiles();
        if (apiKey && settings.speechProvider !== 'local' && this.input('api-key').value === apiKey) this.input('api-key').value = '';
        if (this.savedNote) this.savedNote.textContent = '已保存';
      }
      this.dispatchEvent(new CustomEvent('saved', { detail: saved }));
      return saved;
    } catch (error) {
      if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = error instanceof Error ? error.message : String(error);
      return null;
    } finally {
      this.saving = false;
      releaseSave();
      this.syncBusy();
    }
  }

  private async testConnection(): Promise<void> {
    if (this.testing || this.saving || this.switchingModel || !this.settings) return;
    this.testing = true;
    this.syncBusy();
    const revision = this.viewRevision;
    const signature = this.speechSignature();
    const originalKey = this.input('api-key').value;
    const result = this.root.querySelector<HTMLElement>('#connection-result')!;
    const stale = () => {
      if (!this.isCurrentView(revision)) return true;
      const currentKey = this.input('api-key').value;
      if (signature === this.speechSignature() && (currentKey === originalKey || currentKey === '')) return false;
      result.dataset.state = '';
      result.textContent = '识别配置已更改，请保存并重新测试。';
      return true;
    };
    try {
      const settings = await this.save();
      if (!settings || stale()) return;
      result.dataset.state = '';
      result.textContent = settings.speechProvider === 'local'
        ? '正在等待本地模型就绪并进行静音测试，首次加载可能需要一些时间'
        : '正在检查麦克风和识别链路';
      const response = await this.bridge.request<{ ok: boolean; message: string; modelStatus?: LocalModelStatus | null }>('connection.test', { settings });
      if (stale()) return;
      result.dataset.state = response.ok ? 'ok' : 'error';
      result.textContent = response.message;
      if (settings.speechProvider === 'local') {
        this.localModelStatus = response.modelStatus ?? {
          endpoint: settings.localSpeechEndpoint!, model: settings.localSpeechModel!, connected: response.ok,
          checkedAt: new Date().toISOString(), modelInfo: null,
        };
        this.syncLocalModel();
      }
    } catch (error) {
      if (!stale()) {
        result.dataset.state = 'error';
        result.textContent = error instanceof Error ? error.message : String(error);
        if (this.input('speech-provider').value === 'local') {
          this.localModelStatus = { endpoint: this.input('local-speech-endpoint').value.trim(), model: this.input('local-speech-model').value.trim(), connected: false, checkedAt: new Date().toISOString() };
          this.syncLocalModel();
        }
      }
    } finally {
      this.testing = false;
      this.syncBusy();
    }
  }

  private isCurrentView(revision: number): boolean {
    return revision === this.viewRevision && !this.root.hidden;
  }

  private syncBusy(): void {
    const busy = this.saving || this.testing || this.switchingModel || !this.settings;
    const save = this.root.querySelector<HTMLButtonElement>('[data-action="save"]');
    if (save) save.disabled = this.saving || this.switchingModel || !this.settings;
    for (const test of this.root.querySelectorAll<HTMLButtonElement>('[data-action="test"], [data-action="test-local-model"]')) test.disabled = busy;
    for (const button of this.root.querySelectorAll<HTMLButtonElement>('[data-action="switch-local-model"]')) button.disabled = busy || this.refreshingProfiles || button.dataset.switchable !== 'true';
    const refresh = this.root.querySelector<HTMLButtonElement>('[data-action="refresh-local-profiles"]');
    if (refresh) refresh.disabled = busy || this.refreshingProfiles;
    for (const id of ['speech-provider', 'local-speech-endpoint', 'local-speech-model', 'speech-language']) {
      const input = this.root.querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`);
      if (input) input.disabled = this.switchingModel;
    }
  }

  private syncProvider(): void {
    const local = this.input('speech-provider').value === 'local';
    this.root.querySelector<HTMLElement>('#local-speech-fields')!.hidden = !local;
    this.root.querySelector<HTMLElement>('#cloud-speech-fields')!.hidden = local;
    this.syncLocalModel();
    const result = this.root.querySelector<HTMLElement>('#connection-result')!;
    result.dataset.state = '';
    result.textContent = local
      ? '测试仅向本机服务发送半秒静音，不打开麦克风。准确率与速度请在配置后试说验证。'
      : '测试会打开麦克风约 0.25 秒，再向阿里云确认 Key 和模型可用。';
  }

  private speechSignature(): string {
    return ['speech-provider', 'local-speech-endpoint', 'local-speech-model', 'speech-model', 'speech-model-custom', 'speech-endpoint', 'speech-language', 'microphone', 'vocabulary-id']
      .map((id) => this.input(id).value).join('\n');
  }

  private syncLocalModel(): void {
    const panel = this.root.querySelector<HTMLElement>('#local-model-info');
    if (!panel) return;
    renderLocalModel(panel, this.localModelStatus, this.input('local-speech-endpoint').value, this.input('local-speech-model').value);
    this.syncLocalProfiles();
  }

  private async openLocalModelDocs(model: string): Promise<void> {
    const revision = this.viewRevision;
    try { await this.bridge.request('localAsr.openModelDocs', { model }); }
    catch (error) { if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = String(error); }
  }

  private syncLocalProfiles(): void {
    this.root.querySelector('#local-model-recommendations')?.replaceWith(localModelRecommendations(this.localProfiles, this.settings, this.localModelStatus));
    this.syncBusy();
  }

  private async refreshLocalProfiles(): Promise<void> {
    if (this.refreshingProfiles || this.switchingModel || this.testing || this.saving) return;
    const revision = this.viewRevision;
    this.refreshingProfiles = true;
    this.syncBusy();
    try {
      const profiles = await this.bridge.request<LocalAsrProfileEntry[]>('localAsr.profiles');
      if (!Array.isArray(profiles)) throw new Error('模型列表未能读取，请更新圈说后重试。');
      if (!this.isCurrentView(revision)) return;
      this.localProfiles = profiles;
      this.syncLocalProfiles();
      if (this.savedNote) this.savedNote.textContent = '列表已刷新。已登记的模型可验证并切换。';
    } catch (error) {
      if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = String(error);
    } finally { this.refreshingProfiles = false; this.syncBusy(); }
  }

  private async switchLocalModel(id: string): Promise<void> {
    if (!this.settings || this.switchingModel || this.testing || this.saving || this.refreshingProfiles) return;
    const profile = this.localProfiles?.find(item => item.id === id)?.profile;
    if (!profile) return;
    const revision = this.viewRevision;
    const expectedCurrent = this.settings;
    this.switchingModel = true;
    let release!: () => void;
    this.saveFinished = new Promise<void>(resolve => { release = resolve; });
    this.syncBusy();
    if (this.savedNote) this.savedNote.textContent = '正在验证并切换模型，首次加载可能需要一些时间…';
    try {
      const response = await this.bridge.request<{ settings: AppSettings; modelStatus: LocalModelStatus; message: string }>('localAsr.switchProfile', { id, profile, expectedCurrent });
      if (this.isCurrentView(revision)) {
        this.settings = response.settings;
        this.localModelStatus = response.modelStatus;
        this.value('speech-provider', response.settings.speechProvider ?? 'local');
        this.value('local-speech-endpoint', response.settings.localSpeechEndpoint!);
        this.value('local-speech-model', response.settings.localSpeechModel!);
        this.syncProvider();
        this.syncLocalProfiles();
        const result = this.root.querySelector<HTMLElement>('#connection-result')!;
        result.dataset.state = 'ok';
        result.textContent = response.message;
        if (this.savedNote) this.savedNote.textContent = response.message;
      }
      this.dispatchEvent(new CustomEvent('saved', { detail: response.settings }));
    } catch (error) {
      if (this.isCurrentView(revision)) {
        // 失败后旧模型可能已被网关卸载，历史资料不能继续充当本次身份核验。
        this.localModelStatus = null;
        this.syncLocalModel();
        if (this.savedNote) this.savedNote.textContent = error instanceof Error ? error.message : String(error);
      }
    } finally {
      this.switchingModel = false;
      release();
      this.syncBusy();
    }
  }

  private async copyLocalPrompt(stage: 'assessment' | 'setup', modelId?: string): Promise<void> {
    const revision = this.viewRevision;
    try {
      await this.bridge.request(stage === 'assessment' ? 'localAsr.copyAssessmentPrompt' : 'localAsr.copySetupPrompt', modelId ? { modelId } : undefined);
      if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = stage === 'assessment'
        ? '检测提示词已复制：发给 AI，只检查电脑，不会安装。'
        : '安装提示词已复制：发给 AI 后，它会开始安装和配置。';
    } catch (error) {
      if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = error instanceof Error ? error.message : String(error);
    }
  }

  private async clearKey(): Promise<void> {
    const revision = this.viewRevision;
    try {
      await this.bridge.request('secret.set', { apiKey: '' });
      if (!this.isCurrentView(revision)) return;
      this.input('api-key').value = '';
      const result = this.root.querySelector('#connection-result');
      if (result) result.textContent = '已删除本机保存的 API Key。';
    } catch (error) {
      if (this.isCurrentView(revision) && this.savedNote) this.savedNote.textContent = String(error);
    }
  }

  private section(title: string): HTMLElement {
    const section = element('section', 'settings-section');
    section.append(element('h3', undefined, title));
    return section;
  }

  private field(label: string, controlHtml: string, note?: string): HTMLElement {
    const field = element('div', 'field');
    field.append(element('label', undefined, label));
    field.insertAdjacentHTML('beforeend', controlHtml);
    const control = field.querySelector<HTMLInputElement | HTMLSelectElement>('input[id], select[id]');
    if (control) field.querySelector('label')!.htmlFor = control.id;
    if (note) field.append(element('small', undefined, note));
    return field;
  }

  private note(text: string): HTMLElement {
    return element('p', 'field-note', text);
  }

  private toggle(label: string, id: string, checked: boolean): HTMLElement {
    const row = element('div', 'settings-row');
    const text = element('label', undefined, label);
    text.htmlFor = id;
    row.append(text);
    row.insertAdjacentHTML('beforeend', `<span class="switch"><input id="${id}" type="checkbox" role="switch" ${checked ? 'checked' : ''}><span class="switch-track"></span></span>`);
    return row;
  }

  private button(label: string, action: string, primary = false): HTMLButtonElement {
    const button = element('button', primary ? 'primary-button' : 'secondary-button', label) as HTMLButtonElement;
    button.dataset.action = action;
    return button;
  }

  private input<T extends HTMLInputElement | HTMLSelectElement = HTMLInputElement>(id: string): T {
    const input = this.root.querySelector<T>(`#${id}`);
    if (!input) throw new Error(`设置控件不存在：${id}`);
    return input;
  }

  private value(id: string, value: string): void {
    this.input(id).value = value;
  }
}
