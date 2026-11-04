import type { Bridge, CopyOperation, QuanShuoRecord } from '../bridge/types';
import type { Scene } from '../scene/Scene';
import { formatTime } from '../utils/format';
import { debounce, element, escapeHtml } from '../utils/html';
import { icon } from './icons';

const COPY_OPTIONS: Array<[string, CopyOperation, string]> = [
  ['图片路径', 'image-path', 'link'],
  ['文字文件路径', 'markdown-path', 'file-text'],
  ['文字内容', 'text', 'text'],
  ['图片本身', 'image', 'image'],
];

interface Draft { record: QuanShuoRecord; text: string }

/** 舞台：截图悬在湖面上并投下倒影，右侧检视器放文字和操作。 */
export class StageView extends EventTarget {
  private records: QuanShuoRecord[] = [];
  private editor: HTMLTextAreaElement | null = null;
  private note: HTMLElement | null = null;
  private frame: HTMLImageElement | null = null;
  // 草稿按记录 ID 存放，保存串行执行。保存回调绝不能碰“当前记录”：
  // 用户可能已经切到另一条，否则下一次保存会把 B 的文字写进 A。
  private drafts = new Map<string, Draft>();
  private retrying = new Set<string>();
  private queue: Promise<void> = Promise.resolve();
  private saveFailed = false;
  private saveSoon = debounce(() => { void this.flush(); }, 550);
  private token = 0;

  constructor(private figure: HTMLElement, private inspector: HTMLElement, private bridge: Bridge, private scene: Scene) {
    super();
    inspector.addEventListener('click', (event) => {
      const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-action]');
      if (!button) return;
      const action = button.dataset.action;
      const ids = this.records.map((record) => record.id);
      if (action === 'close') this.dispatchEvent(new Event('closed'));
      if (action === 'copy') this.dispatchEvent(new CustomEvent('copy', { detail: { ids, operation: button.dataset.operation } }));
      if (action === 'retry') void this.retry(button);
      if (action === 'delete') void this.delete(button);
    });
    scene.addEventListener('resized', () => this.syncReflection());
    // 列表收起时舞台变宽，图片跟着移动，倒影要一起走
    new ResizeObserver(() => this.syncReflection()).observe(figure);
    scene.addEventListener('contextrestored', () => this.restoreReflection());
  }

  get isOpen(): boolean {
    return this.records.length > 0;
  }

  /** 等所有草稿落盘。复制、删除、关窗之前都要先调用；返回 false 表示还有文字没保存成功。 */
  async settle(): Promise<boolean> {
    for (let attempt = 0; attempt < 3 && this.drafts.size > 0; attempt++) {
      await this.flush();
      if (this.saveFailed) return false;
    }
    await this.queue;
    return this.drafts.size === 0;
  }

  show(records: QuanShuoRecord[]): void {
    void this.flush();
    this.token += 1;
    this.records = records;
    if (records.length === 0) {
      this.close();
      return;
    }
    this.inspector.hidden = false;
    this.figure.dataset.open = 'true';
    if (records.length === 1) this.renderSingle(records[0]);
    else this.renderMany(records);
  }

  /** 记录从外部变化时更新文字，但不打断正在输入的内容。 */
  refresh(record: QuanShuoRecord): void {
    const index = this.records.findIndex((candidate) => candidate.id === record.id);
    if (index < 0) return;
    this.records[index] = record;
    if (this.editor && this.records.length === 1 && document.activeElement !== this.editor && !this.drafts.has(record.id)) this.editor.value = record.text;
  }

  private close(): void {
    this.inspector.hidden = true;
    this.inspector.replaceChildren();
    this.figure.dataset.open = 'false';
    this.figure.replaceChildren();
    this.editor = null;
    this.note = null;
    this.frame = null;
    this.scene.setStageImage(null, null);
  }

  private renderSingle(record: QuanShuoRecord): void {
    this.renderFigure(record);

    this.inspector.replaceChildren();
    const head = element('header', 'inspector-head');
    const heading = element('div');
    heading.innerHTML = `<div class="inspector-time">${escapeHtml(formatTime(record.timestamp))}</div><div class="inspector-source">${escapeHtml([record.appName, record.windowTitle].filter(Boolean).join(' · '))}</div>`;
    head.append(heading, this.closeButton());

    const body = element('div', 'inspector-body');
    const draft = this.drafts.get(record.id);
    const editor = element('textarea', 'inspector-editor') as HTMLTextAreaElement;
    editor.value = draft?.text ?? record.text;
    editor.readOnly = this.retrying.has(record.id);
    editor.placeholder = '写下你想对 AI 说的话';
    editor.setAttribute('aria-label', '说明文字');
    editor.addEventListener('input', () => {
      this.drafts.set(record.id, { record, text: editor.value });
      this.setNote('正在保存');
      this.saveSoon();
    });
    this.editor = editor;
    this.note = element('div', 'save-note', draft ? '这里有一段还没保存的文字' : record.status === 'incomplete' ? '转写没完成，文字和录音都保留着' : '改动会自动保存');
    body.append(editor, this.note);

    const actions = element('footer', 'inspector-actions');
    const primary = element('button', 'primary-button') as HTMLButtonElement;
    primary.dataset.action = 'copy';
    primary.dataset.operation = 'ai';
    primary.innerHTML = `${icon('copy')}复制给 AI`;
    actions.append(primary);
    if (record.status === 'incomplete' && record.audioPath) {
      const retry = element('button', 'secondary-button') as HTMLButtonElement;
      retry.dataset.action = 'retry';
      retry.innerHTML = `${icon('refresh')}重新转写`;
      retry.disabled = this.retrying.has(record.id);
      if (retry.disabled) retry.textContent = '正在转写';
      actions.append(retry);
    }
    const grid = element('div', 'copy-grid');
    const options = record.imagePath ? COPY_OPTIONS : COPY_OPTIONS.filter(([, operation]) => operation === 'text' || operation === 'markdown-path');
    options.forEach(([label, operation, name]) => {
      const button = element('button', 'copy-option') as HTMLButtonElement;
      button.dataset.action = 'copy';
      button.dataset.operation = operation;
      button.innerHTML = `${icon(name)}<span>${label}</span>`;
      grid.append(button);
    });
    const remove = element('button', 'copy-option danger') as HTMLButtonElement;
    remove.dataset.action = 'delete';
    remove.innerHTML = `${icon('trash')}<span>移入回收站</span>`;
    grid.append(remove);
    actions.append(grid);
    this.inspector.append(head, body, actions);
  }

  private renderFigure(record: QuanShuoRecord): void {
    const token = this.token;
    this.figure.replaceChildren();
    if (!record.imagePath) {
      this.figure.dataset.open = 'false';
      this.frame = null;
      this.scene.setStageImage(null, null);
      return;
    }
    const frame = element('div', 'figure-frame');
    const image = element('img', 'figure-image');
    image.alt = record.text || '这条记录的屏幕截图';
    image.draggable = true;
    image.addEventListener('dragstart', (event) => {
      event.dataTransfer?.setData('text/plain', record.imagePath);
      void this.bridge.request('record.drag', { id: record.id, imagePath: record.imagePath });
    });
    frame.append(image);
    this.figure.append(frame);
    this.frame = image;
    this.imageSource(record).then((source) => {
      if (token !== this.token) return;
      image.src = source;
      return image.decode().then(() => {
        if (token === this.token) this.scene.setStageImage(reflectionTexture(image), this.frameRect());
      });
    }).catch(() => {
      if (token !== this.token) return;
      image.alt = '图片暂时无法读取，请检查文件是否还在原位置。';
      this.scene.setStageImage(null, null);
    });
  }

  private renderMany(records: QuanShuoRecord[]): void {
    const token = this.token;
    this.editor = null;
    this.note = null;
    // 先收掉上一张的倒影。新的前景图加载失败时，湖面不能还留着旧图
    this.scene.setStageImage(null, null);
    this.figure.replaceChildren();
    const stack = element('div', 'figure-stack');
    const shown = records.filter((record) => record.imagePath).slice(0, 3).reverse();
    if (shown.length === 0) this.figure.dataset.open = 'false';
    shown.forEach((record, index) => {
      const image = element('img', `figure-image ${index === shown.length - 1 ? 'front' : 'stacked'}`);
      image.alt = '';
      this.imageSource(record).then((source) => { if (token === this.token) image.src = source; }).catch(() => { image.alt = '图片暂时无法读取'; });
      stack.append(image);
    });
    this.figure.append(stack);
    const front = stack.querySelector<HTMLImageElement>('.front');
    this.frame = front;
    if (front) {
      front.addEventListener('load', () => {
        if (token === this.token) this.scene.setStageImage(reflectionTexture(front), this.frameRect());
      }, { once: true });
    }

    this.inspector.replaceChildren();
    const head = element('header', 'inspector-head');
    const heading = element('div');
    heading.innerHTML = `<div class="inspector-time">${records.length} 条</div><div class="inspector-source">一起复制时按选择顺序编号</div>`;
    head.append(heading, this.closeButton());
    const body = element('div', 'inspector-body');
    const list = element('div', 'selected-list');
    list.innerHTML = `<ol>${records.map((record) => `<li><time>${escapeHtml(formatTime(record.timestamp))}</time>${escapeHtml(record.text || '还没有说明')}</li>`).join('')}</ol>`;
    body.append(list);
    const actions = element('footer', 'inspector-actions');
    const primary = element('button', 'primary-button') as HTMLButtonElement;
    primary.dataset.action = 'copy';
    primary.dataset.operation = 'ai';
    primary.innerHTML = `${icon('copy')}复制 ${records.length} 条给 AI`;
    actions.append(primary);
    this.inspector.append(head, body, actions);
  }

  private closeButton(): HTMLButtonElement {
    const close = element('button', 'icon-button') as HTMLButtonElement;
    close.dataset.action = 'close';
    close.innerHTML = icon('close');
    close.title = '收起（Esc）';
    close.setAttribute('aria-label', '收起');
    return close;
  }

  private frameRect(): DOMRect {
    // 入场动画带位移，取稳定的布局位置，否则倒影和图片对不齐
    const node = this.frame!;
    const box = this.figure.getBoundingClientRect();
    const width = node.offsetWidth, height = node.offsetHeight;
    const left = box.left + (box.width - width) / 2;
    return new DOMRect(left, box.bottom - height, width, height);
  }

  private syncReflection(): void {
    if (this.frame?.complete) this.scene.updateStageRect(this.frameRect());
  }

  /** 上下文恢复后纹理全没了，重新上传。 */
  private restoreReflection(): void {
    if (this.frame?.complete && this.frame.naturalWidth > 0) this.scene.setStageImage(reflectionTexture(this.frame), this.frameRect());
  }

  private setNote(text: string, error = false): void {
    if (!this.note) return;
    this.note.textContent = text;
    this.note.classList.toggle('error', error);
  }

  private flush(): Promise<void> {
    this.queue = this.queue.then(() => this.saveDrafts());
    return this.queue;
  }

  private async saveDrafts(): Promise<void> {
    this.saveFailed = false;
    for (const [id, draft] of [...this.drafts]) {
      const current = (): boolean => this.records.length === 1 && this.records[0].id === id;
      try {
        await this.bridge.request('record.update', { id, markdownPath: draft.record.markdownPath, text: draft.text });
        // 保存期间又输入过的话，新草稿要留到下一轮
        if (this.drafts.get(id)?.text === draft.text) this.drafts.delete(id);
        this.records = this.records.map((record) => record.id === id ? { ...record, text: draft.text } : record);
        if (current() && !this.drafts.has(id)) this.setNote(`已保存 ${formatTime(new Date().toISOString())}`);
        this.dispatchEvent(new CustomEvent('updated', { detail: { id, changes: { text: draft.text } } }));
      } catch (error) {
        // 草稿留在 Map 里：切回这条还能看到，下次保存还会重试
        this.saveFailed = true;
        const message = error instanceof Error ? error.message : String(error);
        if (current()) this.setNote(message, true);
        this.dispatchEvent(new CustomEvent('savefailed', { detail: message }));
      }
    }
  }

  private async retry(button: HTMLButtonElement): Promise<void> {
    const record = this.records[0];
    if (!record || this.retrying.has(record.id)) return;
    this.retrying.add(record.id);
    const editor = this.editor;
    // 转写结果会写回文件，这期间再输入的内容没有地方合并
    if (editor) editor.readOnly = true;
    button.disabled = true;
    button.textContent = '正在转写';
    try {
      if (!(await this.settle())) return;
      const updated = await this.bridge.request<QuanShuoRecord>('record.retry', { id: record.id });
      this.dispatchEvent(new CustomEvent('updated', { detail: { id: updated.id, changes: updated } }));
      if (this.records.length === 1 && this.records[0].id === record.id) this.show([updated]);
    } catch (error) {
      if (this.records.length === 1 && this.records[0].id === record.id) this.setNote(error instanceof Error ? error.message : String(error), true);
    } finally {
      this.retrying.delete(record.id);
      if (this.records.length === 1 && this.records[0].id === record.id) {
        if (this.editor) this.editor.readOnly = false;
        const retry = this.inspector.querySelector<HTMLButtonElement>('[data-action="retry"]');
        if (retry) {
          retry.disabled = false;
          retry.innerHTML = `${icon('refresh')}重新转写`;
        }
      }
    }
  }

  private async delete(button: HTMLButtonElement): Promise<void> {
    const record = this.records[0];
    if (!record) return;
    const label = button.querySelector('span')!;
    if (button.dataset.confirm !== 'true') {
      button.dataset.confirm = 'true';
      button.classList.add('confirm');
      label.textContent = '再点一次确认';
      return;
    }
    if (!(await this.settle())) return;
    try {
      await this.bridge.request('record.delete', { id: record.id });
    } catch (error) {
      button.dataset.confirm = 'false';
      button.classList.remove('confirm');
      label.textContent = '移入回收站';
      this.setNote(error instanceof Error ? error.message : String(error), true);
      return;
    }
    if (this.records.length === 1 && this.records[0].id === record.id) {
      this.records = [];
      this.close();
    }
    this.dispatchEvent(new CustomEvent('deleted', { detail: record }));
  }

  private imageSource(record: QuanShuoRecord): Promise<string> {
    if (record.imageUrl) return Promise.resolve(record.imageUrl);
    return this.bridge.request<string>('record.image', { id: record.id, imagePath: record.imagePath });
  }
}

/** 倒影不需要原图清晰度，缩到小纹理省显存。 */
function reflectionTexture(image: HTMLImageElement): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  const scale = Math.min(1, 640 / Math.max(1, image.naturalWidth));
  canvas.width = Math.max(1, Math.round((image.naturalWidth || image.width) * scale));
  canvas.height = Math.max(1, Math.round((image.naturalHeight || image.height) * scale));
  canvas.getContext('2d')?.drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas;
}
