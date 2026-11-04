import type { AppSettings, Bridge } from '../bridge/types';
import type { SkinText } from '../app/skin';
import { element, escapeHtml } from '../utils/html';

const STEPS = 3;

/** 首次运行。没有卡片，字直接放在画面上。 */
export class OnboardingView extends EventTarget {
  private step = 0;
  private hotkey = 'Ctrl+Alt+G';

  constructor(private root: HTMLElement, private bridge: Bridge, private text: SkinText) {
    super();
    root.addEventListener('click', (event) => {
      const action = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-action]')?.dataset.action;
      if (action === 'next') void this.next();
      if (action === 'back') { this.step = Math.max(0, this.step - 1); this.render(); }
      if (action === 'skip') this.complete();
      if (action === 'capture') void this.capture();
    });
  }

  show(): void {
    this.root.hidden = false;
    this.step = 0;
    this.render();
    void this.bridge.request<AppSettings>('settings.get').then((settings) => {
      this.hotkey = settings.hotkey;
      if (this.step === 0) this.render();
    }).catch(() => undefined);
  }

  private keys(): string {
    // 和列表、湖面底下的快捷键提示同一种写法：键帽挨着，不写加号
    return this.hotkey.split('+').map((key) => `<kbd>${escapeHtml(key)}</kbd>`).join('');
  }

  private render(): void {
    this.root.replaceChildren();
    const panel = element('section', 'onboarding');
    panel.setAttribute('aria-label', '首次运行设置');
    const progress = element('div', 'onboarding-step');
    progress.setAttribute('aria-label', `第 ${this.step + 1} 步，共 ${STEPS} 步`);
    for (let index = 0; index < STEPS; index++) progress.append(element('i', index <= this.step ? 'on' : undefined));
    panel.append(progress);

    if (this.step === 0) {
      panel.append(element('h1', undefined, '圈一下，说一句。'));
      panel.append(element('p', 'onboarding-lead', '看到什么、想改什么，圈出来说给 AI 听。图片和文字会一起保存，一键就能发出去。'));
      const keys = element('div', 'onboarding-keys');
      keys.innerHTML = `${this.keys()}<span>随时开始圈选</span>`;
      panel.append(keys);
    } else if (this.step === 1) {
      panel.append(element('h1', undefined, '让声音变成文字。'));
      panel.append(element('p', 'onboarding-lead', '可以使用阿里云识别，也可以稍后在设置中配置本地服务。云端模式只上传音频；本地模式不需要 Key。截图和文字都留在这台电脑上。'));
      const field = element('div', 'field');
      field.innerHTML = '<label for="onboarding-key">阿里云 API Key</label><input class="text-input" id="onboarding-key" type="password" autocomplete="off" placeholder="可以跳过，之后在设置里填"><small>用 Windows 加密保存，不写入任何配置文件。</small>';
      panel.append(field);
    } else {
      panel.append(element('h1', undefined, this.text.onboardingTitle));
      panel.append(element('p', 'onboarding-lead', this.text.onboardingLead));
      const note = element('p', 'onboarding-note', `之后随时按 ${this.hotkey} 开始。`);
      note.id = 'capture-note';
      panel.append(note);
    }

    const actions = element('div', 'onboarding-actions');
    if (this.step === 2) {
      const capture = element('button', 'primary-button', '现在试一次') as HTMLButtonElement;
      capture.dataset.action = 'capture';
      const done = element('button', 'secondary-button', '完成') as HTMLButtonElement;
      done.dataset.action = 'next';
      actions.append(capture, done);
    } else {
      const next = element('button', 'primary-button', '继续') as HTMLButtonElement;
      next.dataset.action = 'next';
      actions.append(next);
    }
    const ghost = element('button', 'ghost', this.step === 0 ? '跳过' : '上一步') as HTMLButtonElement;
    ghost.dataset.action = this.step === 0 ? 'skip' : 'back';
    actions.append(ghost);
    panel.append(actions);
    this.root.append(panel);
  }

  private async next(): Promise<void> {
    if (this.step === 1) {
      const key = this.root.querySelector<HTMLInputElement>('#onboarding-key')?.value ?? '';
      if (key) await this.bridge.request('secret.set', { apiKey: key });
    }
    if (this.step === STEPS - 1) {
      this.complete();
      return;
    }
    this.step += 1;
    this.render();
  }

  private async capture(): Promise<void> {
    await this.bridge.request('capture.test');
    const note = this.root.querySelector('#capture-note');
    if (note) note.textContent = this.text.onboardingCaptured;
  }

  private complete(): void {
    this.root.hidden = true;
    void this.bridge.request('onboarding.complete');
    this.dispatchEvent(new Event('completed'));
  }
}
