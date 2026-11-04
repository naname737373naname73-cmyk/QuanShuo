import type { SkinText } from '../app/skin';
import type { Bridge, QuanShuoRecord } from '../bridge/types';
import { dayKey, formatDay, formatTime } from '../utils/format';
import { element, escapeHtml } from '../utils/html';
import { icon } from './icons';

type Row = { key: string; type: 'date' | 'record'; label: string; record?: QuanShuoRecord; offset: number; height: number };

/** 虚拟列表。滚动时复用已有节点，缩略图才不会闪。 */
export class RecordList extends EventTarget {
  private rows: Row[] = [];
  private spacer: HTMLDivElement;
  private mounted = new Map<string, HTMLElement>();
  private selected = new Set<string>();
  private thumbnails = new Map<string, Promise<string>>();
  private hovered: string | null = null;
  private recordHeight = 92;
  private dateHeight = 40;
  private message = false;

  constructor(private scroll: HTMLElement, private activeDate: HTMLElement, private bridge: Bridge, private text: SkinText) {
    super();
    this.spacer = element('div', 'record-spacer');
    scroll.append(this.spacer);
    this.readSizes();
    scroll.addEventListener('scroll', () => this.renderVisible(), { passive: true });
    new ResizeObserver(() => this.renderVisible()).observe(scroll);
  }

  setRecords(records: QuanShuoRecord[]): void {
    this.readSizes();
    this.clearMessage();
    this.rows = [];
    let previousDay = '';
    let offset = 0;
    records.forEach((record) => {
      const day = dayKey(record.timestamp);
      if (day !== previousDay) {
        // 第一组的日期由列表顶部的吸顶标签显示，这里不占高度
        const height = previousDay ? this.dateHeight : 0;
        this.rows.push({ key: `date-${day}`, type: 'date', label: formatDay(record.timestamp), offset, height });
        offset += height;
        previousDay = day;
      }
      this.rows.push({ key: record.id, type: 'record', label: '', record, offset, height: this.recordHeight });
      offset += this.recordHeight;
    });
    // 末尾留出底边淡出的高度（history.css 的 .record-scroll）
    this.spacer.style.height = `${offset + 22}px`;
    this.activeDate.dataset.empty = String(records.length === 0);
    // 记录内容可能变了，全部重建，已缓存的缩略图不会重新请求
    this.mounted.forEach((node) => node.remove());
    this.mounted.clear();
    this.renderVisible();
  }

  setEmpty(search: boolean, hotkey: string): void {
    this.showMessage();
    const empty = element('div', 'empty-state');
    const keys = hotkey.split('+').map((key) => `<kbd>${escapeHtml(key)}</kbd>`).join('');
    empty.innerHTML = search
      ? '<h2>没有找到这句话</h2><p>换一个词试试，或清空搜索看全部记录。</p>'
      : `<img src="${this.text.emptyImage}" alt=""><h2>${this.text.emptyTitle}</h2><p>${this.text.emptyLine}</p><div class="hotkey-line">${keys}</div>`;
    this.spacer.append(empty);
  }

  setLoading(): void {
    this.showMessage();
    const loading = element('div', 'loading-state');
    loading.setAttribute('aria-label', '正在加载记录');
    for (let index = 0; index < 6; index++) loading.append(element('div', 'skeleton'));
    this.spacer.append(loading);
  }

  setSelected(ids: Set<string>): void {
    this.selected = new Set(ids);
    this.mounted.forEach((node, key) => {
      node.querySelector('.record-item')?.setAttribute('aria-pressed', String(this.selected.has(key)));
    });
  }

  setStarHover(id: string | null): void {
    this.hovered = id;
    this.mounted.forEach((node, key) => {
      const item = node.querySelector<HTMLElement>('.record-item');
      if (item) item.dataset.starHover = String(key === id);
    });
  }

  scrollToRecord(id: string): void {
    const row = this.rows.find((candidate) => candidate.key === id);
    if (!row) return;
    const top = row.offset, bottom = row.offset + row.height;
    if (top >= this.scroll.scrollTop && bottom <= this.scroll.scrollTop + this.scroll.clientHeight) return;
    this.scroll.scrollTop = Math.max(0, top - this.scroll.clientHeight / 2 + row.height / 2);
    this.renderVisible();
  }

  scrollToDate(date: string): void {
    const row = this.rows.find((candidate) => candidate.key === `date-${date}`);
    if (row) this.scroll.scrollTop = row.offset;
  }

  private showMessage(): void {
    this.mounted.forEach((node) => node.remove());
    this.mounted.clear();
    this.rows = [];
    this.spacer.replaceChildren();
    this.spacer.style.height = '100%';
    this.activeDate.dataset.empty = 'true';
    this.message = true;
  }

  private clearMessage(): void {
    if (!this.message) return;
    this.spacer.replaceChildren();
    this.message = false;
  }

  private renderVisible(): void {
    if (this.rows.length === 0) return;
    const scrollTop = this.scroll.scrollTop;
    this.activeDate.dataset.scrolled = String(scrollTop > 10);
    const start = scrollTop - this.recordHeight * 3;
    const end = scrollTop + this.scroll.clientHeight + this.recordHeight * 3;
    const visible = this.rows.filter((row) => row.offset + row.height >= start && row.offset <= end);
    const wanted = new Set(visible.map((row) => row.key));
    this.mounted.forEach((node, key) => {
      if (wanted.has(key)) return;
      node.remove();
      this.mounted.delete(key);
    });
    visible.forEach((row) => {
      if (this.mounted.has(row.key)) return;
      const node = this.createRow(row);
      this.mounted.set(row.key, node);
      this.spacer.append(node);
    });
    const passed = this.rows.filter((row) => row.type === 'date' && row.offset <= scrollTop + 6);
    const current = passed[passed.length - 1] ?? this.rows.find((row) => row.type === 'date');
    const label = this.activeDate.querySelector('span');
    if (current && label) label.textContent = current.label;
  }

  private createRow(row: Row): HTMLElement {
    const container = element('div', `record-row${row.type === 'date' ? ' date-row' : ''}`);
    container.style.transform = `translateY(${row.offset}px)`;
    container.style.height = `${row.height}px`;
    if (row.type === 'date') {
      if (row.height === 0) container.style.visibility = 'hidden';
      container.textContent = row.label;
    } else if (row.record) {
      container.append(this.createItem(row.record));
    }
    return container;
  }

  private createItem(record: QuanShuoRecord): HTMLElement {
    const item = element('div', 'record-item');
    item.dataset.id = record.id;
    item.dataset.starHover = String(record.id === this.hovered);
    item.setAttribute('role', 'button');
    item.setAttribute('tabindex', '0');
    item.setAttribute('aria-pressed', String(this.selected.has(record.id)));
    item.setAttribute('aria-label', `${formatTime(record.timestamp)}，${record.text || '还没有说明'}，来自 ${record.appName}`);

    const thumbnail = record.imagePath ? this.createThumbnail(record) : this.createVoiceTile();
    const main = element('div', 'record-main');
    const flag = record.status === 'incomplete'
      ? '<span class="status-flag">转写未完成</span>'
      : record.status === 'recording' ? '<span class="status-flag recording">正在听</span>' : '';
    const text = record.text
      ? `<div class="record-text">${escapeHtml(record.text)}</div>`
      : '<div class="record-text placeholder">还没有说明，点开写一句</div>';
    main.innerHTML = `${text}<div class="record-meta"><span class="record-time">${escapeHtml(formatTime(record.timestamp))}</span><span class="record-app">${escapeHtml(record.appName)}</span>${flag}</div>`;

    const check = element('span', 'record-check');
    check.innerHTML = icon('check');

    const quick = element('button', 'record-quick') as HTMLButtonElement;
    quick.innerHTML = icon('copy');
    quick.title = '复制给 AI';
    quick.setAttribute('aria-label', '复制这条记录给 AI');
    quick.addEventListener('click', (event) => {
      event.stopPropagation();
      this.dispatchEvent(new CustomEvent('copy', { detail: record.id }));
    });

    item.append(check, thumbnail, main, quick);
    item.addEventListener('pointerenter', () => this.dispatchEvent(new CustomEvent('itemhover', { detail: record.id })));
    item.addEventListener('pointerleave', () => this.dispatchEvent(new CustomEvent('itemhover', { detail: null })));
    item.addEventListener('click', (event) => {
      this.dispatchEvent(new CustomEvent('select', { detail: { id: record.id, ctrl: event.ctrlKey || event.metaKey, shift: event.shiftKey } }));
    });
    item.addEventListener('keydown', (event) => {
      if (event.target !== item) return;
      if (event.key === 'Enter') {
        event.preventDefault();
        this.dispatchEvent(new CustomEvent('select', { detail: { id: record.id, ctrl: event.ctrlKey || event.metaKey, shift: event.shiftKey } }));
      }
      if (event.key === ' ') {
        event.preventDefault();
        this.dispatchEvent(new CustomEvent('select', { detail: { id: record.id, ctrl: true, shift: false } }));
      }
    });
    return item;
  }

  private createThumbnail(record: QuanShuoRecord): HTMLElement {
    const thumbnail = element('img', 'record-thumb');
    thumbnail.alt = '';
    thumbnail.loading = 'lazy';
    thumbnail.draggable = true;
    thumbnail.addEventListener('dragstart', (event) => {
      event.dataTransfer?.setData('text/plain', record.imagePath);
      void this.bridge.request('record.drag', { id: record.id, imagePath: record.imagePath });
    });
    this.thumbnail(record).then((source) => { thumbnail.src = source; }).catch(() => { thumbnail.alt = '缩略图暂时无法读取'; });
    return thumbnail;
  }

  /** 只说话的记录没有截图，放一个麦克风占位，列表才对得齐。 */
  private createVoiceTile(): HTMLElement {
    const tile = element('div', 'record-thumb voice-tile');
    tile.innerHTML = icon('mic');
    return tile;
  }

  private thumbnail(record: QuanShuoRecord): Promise<string> {
    if (record.imageUrl) return Promise.resolve(record.imageUrl);
    const existing = this.thumbnails.get(record.id);
    if (existing) return existing;
    const request = this.bridge.request<string>('record.thumbnail', { id: record.id, imagePath: record.imagePath, width: 240 });
    this.thumbnails.set(record.id, request);
    return request;
  }

  private readSizes(): void {
    const styles = getComputedStyle(document.documentElement);
    this.recordHeight = Number.parseInt(styles.getPropertyValue('--record-height'), 10) || 92;
    this.dateHeight = Number.parseInt(styles.getPropertyValue('--date-height'), 10) || 40;
  }
}
