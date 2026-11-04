import type { AppSettings, Bridge, CopyOperation, QuanShuoRecord } from '../bridge/types';
import { clampDay, dayFromClock, DUSK, NIGHT, type QueryOptions } from './query';
import { SKIN_TEXT, type SkinText } from './skin';
import { RecordList } from '../components/RecordList';
import { StageView } from '../components/StageView';
import { SettingsView } from '../components/SettingsView';
import { CalendarView } from '../components/CalendarView';
import { OnboardingView } from '../components/OnboardingView';
import { DebugPanel } from '../components/DebugPanel';
import { icon } from '../components/icons';
import { BloomScene } from '../scene/BloomScene';
import type { Scene } from '../scene/Scene';
import { SceneRenderer } from '../scene/SceneRenderer';
import config from '../config/scene.config.json';
import { countToday, dayKey, formatManyForAI, joinTexts, formatTime } from '../utils/format';
import { debounce, element, escapeHtml } from '../utils/html';

const JOURNAL_KEY = 'quanshuo.journal';
const RESUME_KEY = 'quanshuo.resume';

/** 换皮肤时整页重开，这些内存里的状态要带过去。 */
interface Resume { search: string; selected: string[]; marked: string[]; managing: boolean }

// 只是一个顺手的记忆，存储不可用时按展开处理
function readJournalOpen(): boolean {
  try { return localStorage.getItem(JOURNAL_KEY) !== 'closed'; } catch { return true; }
}

function writeJournalOpen(open: boolean): void {
  try { localStorage.setItem(JOURNAL_KEY, open ? 'open' : 'closed'); } catch { /* 下次打开时回到展开 */ }
}

/** 组装界面，持有记录和选择状态。 */
export class App {
  /** 列表显示的记录，搜索时是过滤后的结果。 */
  private records: QuanShuoRecord[] = [];
  /** 全部记录。星座、选择和复制都按它解析，否则搜索后点星星会选中一条“不存在”的记录。 */
  private allRecords: QuanShuoRecord[] = [];
  private loadVersion = 0;
  private editVersion = 0;
  private selected = new Set<string>();
  private selectionAnchor: string | null = null;
  /** 批量选择时勾上的记录。和 selected 分开，勾选不会打开详情。 */
  private marked = new Set<string>();
  private markAnchor: string | null = null;
  private managing = false;
  private deleteConfirmTimer = 0;
  private calendar: CalendarView;
  private list: RecordList;
  private stage: StageView;
  private settings: SettingsView;
  private onboarding: OnboardingView;
  private scene: Scene;
  private debug: DebugPanel | null = null;
  private text: SkinText;
  private switchingSkin = false;
  private shownDay = dayKey(new Date().toISOString());
  private searchText = '';
  private hotkey = 'Ctrl+Alt+G';
  private speechHotkey = 'Ctrl+Alt+H';
  private listeningTimer = 0;
  private toastTimer = 0;
  private followClock: boolean;

  constructor(private root: HTMLElement, private bridge: Bridge, private options: QueryOptions) {
    this.followClock = options.followClock;
    this.text = SKIN_TEXT[options.skin];
    root.innerHTML = this.template();
    this.setJournalOpen(readJournalOpen());
    document.documentElement.style.setProperty('--horizon', `${config.horizon * 100}vh`);
    this.scene = options.skin === 'crabapple'
      ? new BloomScene(this.require('.bloom'), this.require('.petals'), options)
      : new SceneRenderer(this.require('.lake'), this.require('.lake-fallback'), options);
    this.list = new RecordList(this.require('.record-scroll'), this.require('.active-date'), bridge, this.text);
    this.stage = new StageView(this.require('.stage-figure'), this.require('.inspector'), bridge, this.scene);
    this.calendar = new CalendarView(this.require('.calendar-pop'), this.require('.date-jump'), options.skin);
    this.settings = new SettingsView(this.require('.settings-view'), bridge);
    this.settings.beforeSkinChange = () => this.stage.settle();
    this.onboarding = new OnboardingView(this.require('.onboarding-view'), bridge, this.text);
    if (this.scene instanceof SceneRenderer) this.debug = new DebugPanel(this.require('.debug-panel'), this.scene, options.debug);
    this.bindEvents();
    this.require('.day-control output').textContent = config.periods[Math.round(options.day) % config.periods.length].label;
    // 窗口可能开一整晚，每分钟跟一次系统时间
    window.setInterval(() => this.onMinute(), 60_000);
  }

  async initialize(): Promise<void> {
    const sceneReady = this.scene.initialize();
    void this.bridge.request<AppSettings>('settings.get').then((settings) => this.applyHotkeys(settings)).catch(() => undefined);
    if (this.options.state === 'loading') {
      this.list.setLoading();
      await sceneReady;
      document.body.dataset.appReady = 'true';
      return;
    }
    const resume = this.takeResume();
    if (resume?.search) {
      this.searchText = resume.search;
      this.require<HTMLInputElement>('#history-search').value = resume.search;
    }
    await Promise.all([this.loadRecords(), sceneReady]);
    this.applyInitialState();
    if (resume) this.applyResume(resume);
    document.body.dataset.appReady = 'true';
  }

  private async loadRecords(animate = false): Promise<void> {
    const loadVersion = ++this.loadVersion;
    const editVersion = this.editVersion;
    const [all, filtered] = await Promise.all([
      this.bridge.request<QuanShuoRecord[]>('records.query', { query: '' }),
      this.searchText ? this.bridge.request<QuanShuoRecord[]>('records.query', { query: this.searchText }) : Promise.resolve(null),
    ]);
    if (loadVersion !== this.loadVersion) return;
    if (editVersion !== this.editVersion) return this.loadRecords(animate);
    this.allRecords = this.options.state === 'empty' ? [] : all;
    if (this.options.state === 'failed' && this.allRecords[0]) this.allRecords[0] = { ...this.allRecords[0], status: 'incomplete' };
    if (this.options.state === 'recording' && this.allRecords[0]) this.allRecords[0] = { ...this.allRecords[0], status: 'recording' };
    this.records = filtered ?? this.allRecords;
    this.renderRecords(animate);
  }

  private renderRecords(animate = false): void {
    const today = countToday(this.allRecords);
    this.require('#star-count').textContent = String(today);
    this.require('.today-day').textContent = new Intl.DateTimeFormat('zh-CN', { month: 'long', day: 'numeric', weekday: 'short' }).format(new Date());
    if (this.options.state === 'search-empty' || (this.searchText && this.records.length === 0)) this.list.setEmpty(true, this.hotkey);
    else if (this.records.length === 0) this.list.setEmpty(false, this.hotkey);
    else this.list.setRecords(this.records);

    const valid = new Map(this.allRecords.map((record) => [record.id, record]));
    const before = this.selected.size;
    this.selected = new Set([...this.selected].filter((id) => valid.has(id)));
    this.marked = new Set([...this.marked].filter((id) => valid.has(id)));
    this.list.setSelected(this.highlighted);
    if (before !== this.selected.size) this.openSelection();
    else this.selected.forEach((id) => this.stage.refresh(valid.get(id)!));
    this.updateSummary();
    this.refreshMarks();
    this.calendar.setRecords(this.allRecords);
    this.scene.setRecords(this.allRecords, animate);
    this.scene.setSelected(this.selected);
    this.require('.stage-hint').dataset.hidden = String(this.selected.size > 0);
  }

  private applyInitialState(): void {
    const first = this.records[0];
    switch (this.options.state) {
      case 'selected':
        this.selected = new Set(this.records.slice(0, 3).map((record) => record.id));
        this.afterSelection();
        break;
      case 'hover':
        if (first) { this.scene.setHovered(first.id); this.list.setStarHover(first.id); }
        break;
      case 'detail':
        if (first) { this.selected = new Set([first.id]); this.afterSelection(); }
        break;
      case 'settings': void this.settings.show(); break;
      case 'onboarding': this.showOnboarding(); break;
      case 'recording':
        this.showListening('这里的按钮颜色太重了，我想换成更浅的蓝');
        this.scene.setForcedVoice(this.options.wind ?? 0.75);
        break;
      case 'failed': this.toast('识别连接中断了。文字和录音都保留着，打开记录可以重新转写。', { kind: 'error' }); break;
      default: break;
    }
  }

  private bindEvents(): void {
    this.bindTitlebar();
    const search = this.require<HTMLInputElement>('#history-search');
    const runSearch = debounce(() => {
      this.searchText = search.value.trim();
      void this.loadRecords();
    }, 220);
    search.addEventListener('input', runSearch);
    this.require('.settings-button').addEventListener('click', () => void this.settings.show());
    this.require('.speech-button').addEventListener('click', () => void this.bridge.request('speech.open'));
    this.require('.journal-toggle').addEventListener('click', () => this.setJournalOpen(!this.journalOpen, true));
    this.require('.date-jump').addEventListener('click', () => this.calendar.toggle());
    this.calendar.addEventListener('pick', (event) => void this.jumpToDay((event as CustomEvent<string>).detail));
    this.require('.manage-button').addEventListener('click', () => this.setManaging(!this.managing));
    this.require('.manage-all').addEventListener('click', () => this.markAll());
    this.require('.manage-incomplete').addEventListener('click', () => this.markIncomplete());
    this.require('.manage-cancel').addEventListener('click', () => this.setManaging(false));
    this.require('.manage-copy-ai').addEventListener('click', () => void this.copyMarked('ai'));
    this.require('.manage-copy-text').addEventListener('click', () => void this.copyMarked('text'));
    this.require('.manage-delete').addEventListener('click', () => void this.deleteMarked());

    this.list.addEventListener('select', (event) => {
      const detail = (event as CustomEvent<{ id: string; ctrl: boolean; shift: boolean }>).detail;
      if (this.managing) this.toggleMark(detail.id, detail.shift);
      else this.select(detail.id, detail.ctrl, detail.shift);
    });
    this.list.addEventListener('copy', (event) => void this.copy([(event as CustomEvent<string>).detail], 'ai'));
    this.list.addEventListener('itemhover', (event) => this.scene.setHovered((event as CustomEvent<string | null>).detail));

    this.scene.addEventListener('starhover', (event) => {
      const detail = (event as CustomEvent<{ id: string; x: number; y: number } | null>).detail;
      this.list.setStarHover(detail?.id ?? null);
      this.showStarTip(detail);
    });
    this.scene.addEventListener('starselect', (event) => {
      const id = (event as CustomEvent<string>).detail;
      this.list.scrollToRecord(id);
      if (this.managing) this.toggleMark(id, false);
      else this.select(id, false, false);
    });
    this.scene.addEventListener('period', (event) => {
      const { id } = (event as CustomEvent<{ id: string }>).detail;
      const output = this.require('.day-control output');
      output.textContent = config.periods.find((period) => period.id === id)?.label ?? '';
    });

    this.require('.copy-ai').addEventListener('click', () => {
      const ids = this.selected.size > 0 ? [...this.selected] : this.allRecords[0] ? [this.allRecords[0].id] : [];
      void this.copy(ids, 'ai');
    });
    this.stage.addEventListener('copy', (event) => {
      const detail = (event as CustomEvent<{ ids: string[]; operation: CopyOperation }>).detail;
      void this.copy(detail.ids, detail.operation);
    });
    this.stage.addEventListener('closed', () => this.clearSelection());
    this.stage.addEventListener('updated', (event) => {
      this.editVersion += 1;
      const { id, changes } = (event as CustomEvent<{ id: string; changes: Partial<QuanShuoRecord> }>).detail;
      const apply = (record: QuanShuoRecord) => record.id === id ? { ...record, ...changes } : record;
      this.allRecords = this.allRecords.map(apply);
      this.records = this.searchText ? this.records.map(apply) : this.allRecords;
      this.renderRecords();
    });
    this.stage.addEventListener('savefailed', (event) => this.toast(`文字没有保存：${(event as CustomEvent<string>).detail}`, { kind: 'error' }));
    this.stage.addEventListener('deleted', (event) => {
      const deleted = (event as CustomEvent<QuanShuoRecord>).detail;
      this.selected.delete(deleted.id);
      void this.loadRecords().then(() => this.toast('已移入回收站。', {
        undo: async () => {
          await this.bridge.request('record.undo', { id: deleted.id });
          await this.loadRecords();
          this.toast('这条记录找回来了。');
        },
      }));
    });

    this.onboarding.addEventListener('completed', () => { this.require('.app').dataset.onboarding = 'false'; });
    this.settings.addEventListener('saved', (event) => {
      const settings = (event as CustomEvent<AppSettings>).detail;
      this.applySettings(settings);
      this.toast('设置已保存。');
    });
    this.bridge.subscribe('records.changed', () => void this.loadRecords());
    this.bridge.subscribe('settings.changed', (payload) => {
      if (typeof payload === 'object' && payload !== null) this.applySettings(payload as AppSettings);
    });
    this.bridge.subscribe('recording.level', (payload) => {
      if (typeof payload === 'number') {
        this.scene.setMicrophoneLevel(payload);
        this.keepListening();
      }
    });
    this.bridge.subscribe('transcript.delta', (payload) => {
      const delta = payload as { text?: string; isFinal?: boolean };
      this.showListening(delta.text ?? '');
      if (delta.isFinal) {
        window.clearTimeout(this.listeningTimer);
        this.listeningTimer = window.setTimeout(() => this.hideListening(), 1400);
      }
    });
    this.bridge.subscribe('records.created', () => {
      void this.loadRecords(true);
    });
    this.bridge.subscribe('error', (payload) => {
      const message = (payload as { message?: string }).message;
      if (message) this.toast(message, { kind: 'error' });
    });
    this.bridge.subscribe('window.closeRequested', () => void this.closeWindow());
    // 窗口已经开着时原生不再重新导航（会丢掉还没落盘的草稿和勾选），由这里切到要的界面
    this.bridge.subscribe('view.open', (payload) => {
      const state = (payload as { state?: string }).state;
      if (state === 'settings') void this.settings.show();
      else if (state === 'onboarding') this.showOnboarding();
    });
    this.bridge.subscribe('window.state', (payload) => {
      const maximized = (payload as { maximized?: boolean }).maximized === true;
      this.require('.maximize-button').innerHTML = icon(maximized ? 'restore' : 'maximize');
      document.documentElement.dataset.maximized = String(maximized);
    });

    document.addEventListener('keydown', (event) => this.onKeyDown(event));
  }

  private showOnboarding(): void {
    this.require('.app').dataset.onboarding = 'true';
    this.onboarding.show();
  }

  private bindTitlebar(): void {
    const slider = this.require<HTMLInputElement>('.day-slider');
    slider.value = String(this.options.day);
    slider.addEventListener('input', () => {
      // 手动拖过之后，这次窗口不再跟随系统时间
      this.followClock = false;
      this.scene.setDay(Number(slider.value), true);
    });
    // 语音框是原生窗口，手动拖过之后它也要跟着湖面。跟随系统时间时两边各算各的，结果一样
    slider.addEventListener('change', () => void this.bridge.request('scene.day', { day: Number(slider.value) }));
    this.require('.minimize-button').addEventListener('click', () => void this.bridge.request('window.minimize'));
    this.require('.maximize-button').addEventListener('click', () => void this.bridge.request('window.toggleMaximize'));
    this.require('.close-button').addEventListener('click', () => void this.closeWindow());
  }

  /** 所有关窗入口都走这里：文字落盘后才销毁 WebView2。 */
  private async closeWindow(): Promise<void> {
    if (await this.stage.settle()) {
      await this.bridge.request('window.close');
      return;
    }
    await this.bridge.request('window.keepOpen');
    this.toast('还有文字没保存成功，窗口先不关。', { kind: 'error' });
  }

  private onKeyDown(event: KeyboardEvent): void {
    const key = event.key.toLowerCase();
    if (event.ctrlKey && event.shiftKey && key === 'd') {
      event.preventDefault();
      this.debug?.toggle();
    } else if (event.ctrlKey && key === 'a' && this.managing && !this.isEditing(event.target)) {
      event.preventDefault();
      this.markAll();
    } else if (event.ctrlKey && key === 'c' && !this.isEditing(event.target)) {
      event.preventDefault();
      if (this.managing) void this.copyMarked('ai');
      else this.require<HTMLButtonElement>('.copy-ai').click();
    } else if (event.ctrlKey && key === 'f') {
      event.preventDefault();
      this.setJournalOpen(true, true);
      this.require<HTMLInputElement>('#history-search').focus();
    } else if (event.ctrlKey && key === 'b') {
      event.preventDefault();
      this.setJournalOpen(!this.journalOpen, true);
    } else if (event.key === 'Escape') {
      if (this.calendar.isOpen) this.calendar.hide();
      else if (!this.require('.settings-view').hidden) this.settings.hide();
      else if (this.managing) this.setManaging(false);
      else if (this.stage.isOpen) this.clearSelection();
    }
  }

  /** 列表高亮的是哪一组：批量选择时是勾选，平时是选中。 */
  private get highlighted(): Set<string> {
    return this.managing ? this.marked : this.selected;
  }

  private get journalOpen(): boolean {
    return this.require('.app').dataset.journal !== 'closed';
  }

  /** 把列表收到屏幕外，湖景和舞台占满窗口。 */
  private setJournalOpen(open: boolean, remember = false): void {
    this.require('.app').dataset.journal = open ? 'open' : 'closed';
    const toggle = this.require<HTMLButtonElement>('.journal-toggle');
    const label = open ? '收起列表（Ctrl+B）' : '展开列表（Ctrl+B）';
    toggle.title = label;
    toggle.setAttribute('aria-label', label);
    toggle.setAttribute('aria-expanded', String(open));
    if (remember) writeJournalOpen(open);
  }

  private select(id: string, ctrl: boolean, shift: boolean): void {
    if (shift && this.selectionAnchor) {
      const start = this.records.findIndex((record) => record.id === this.selectionAnchor);
      const end = this.records.findIndex((record) => record.id === id);
      if (start < 0 || end < 0) {
        // 锚点被搜索过滤掉了，这次按普通单选处理，并把锚点重置到这里
        this.selected = new Set([id]);
        this.selectionAnchor = id;
      } else {
        const [from, to] = start < end ? [start, end] : [end, start];
        this.selected = new Set(this.records.slice(from, to + 1).map((record) => record.id));
      }
    } else if (ctrl) {
      if (this.selected.has(id)) this.selected.delete(id);
      else this.selected.add(id);
      this.selectionAnchor = id;
    } else if (this.selected.size === 1 && this.selected.has(id)) {
      // 再点一次同一条：收起舞台，把湖景还给用户
      this.clearSelection();
      return;
    } else {
      this.selected = new Set([id]);
      this.selectionAnchor = id;
    }
    this.afterSelection();
  }

  private clearSelection(): void {
    this.selected = new Set();
    this.afterSelection();
  }

  /** 进入或退出批量选择。进入时收起详情，点列表就是勾选。 */
  private setManaging(on: boolean): void {
    this.managing = on;
    this.marked = new Set();
    this.markAnchor = null;
    this.require('.journal').dataset.managing = String(on);
    this.require('.manage-button').setAttribute('aria-pressed', String(on));
    if (on && this.selected.size > 0) this.clearSelection();
    this.list.setSelected(this.highlighted);
    this.resetDeleteConfirm();
    this.refreshMarks();
  }

  /** 正在录音的记录内容还没定，不能勾选。 */
  private pickable(records: QuanShuoRecord[]): QuanShuoRecord[] {
    return records.filter((record) => record.status !== 'recording');
  }

  private toggleMark(id: string, shift: boolean): void {
    const record = this.allRecords.find((candidate) => candidate.id === id);
    if (!record) return;
    if (record.status === 'recording') {
      this.toast('这条还在录音，结束后才能选。');
      return;
    }
    const start = shift && this.markAnchor ? this.records.findIndex((candidate) => candidate.id === this.markAnchor) : -1;
    const end = this.records.findIndex((candidate) => candidate.id === id);
    if (start >= 0 && end >= 0) {
      const [from, to] = start < end ? [start, end] : [end, start];
      this.pickable(this.records.slice(from, to + 1)).forEach((candidate) => this.marked.add(candidate.id));
    } else if (this.marked.has(id)) {
      this.marked.delete(id);
    } else {
      this.marked.add(id);
    }
    this.markAnchor = id;
    this.refreshMarks();
  }

  /** 全选当前列表，搜索时只选搜到的。已经全选时再点就是全不选。 */
  private markAll(): void {
    const ids = this.pickable(this.records).map((record) => record.id);
    this.marked = ids.length > 0 && ids.every((id) => this.marked.has(id)) ? new Set() : new Set(ids);
    this.refreshMarks();
  }

  private markIncomplete(): void {
    this.marked = new Set(this.records.filter((record) => record.status === 'incomplete').map((record) => record.id));
    this.refreshMarks();
  }

  private refreshMarks(): void {
    if (!this.managing) return;
    this.resetDeleteConfirm();
    this.list.setSelected(this.marked);
    const ids = this.pickable(this.records).map((record) => record.id);
    const allMarked = ids.length > 0 && ids.every((id) => this.marked.has(id));
    this.require('.manage-all').textContent = allMarked ? '全不选' : '全选';
    this.require<HTMLButtonElement>('.manage-incomplete').disabled = !this.records.some((record) => record.status === 'incomplete');
    this.require('.manage-count').textContent = this.marked.size > 0 ? `已选 ${this.marked.size} 条` : '';
    this.require<HTMLButtonElement>('.manage-copy-ai').disabled = this.marked.size === 0;
    this.require<HTMLButtonElement>('.manage-copy-text').disabled = this.marked.size === 0;
    const button = this.require<HTMLButtonElement>('.manage-delete');
    button.disabled = this.marked.size === 0;
    button.textContent = this.marked.size > 0 ? `删除 ${this.marked.size} 条` : '删除';
  }

  private resetDeleteConfirm(): void {
    window.clearTimeout(this.deleteConfirmTimer);
    this.require('.manage-delete').dataset.confirm = 'false';
  }

  /** 复制完留在批量选择里，接着换一种复制方式或者删掉都方便。 */
  private async copyMarked(operation: 'ai' | 'text'): Promise<void> {
    if (this.marked.size === 0) {
      this.toast('先勾选要复制的记录。');
      return;
    }
    await this.copy([...this.marked], operation);
  }

  /** 第一下只是确认提示，第二下才删。删错了还能撤销。 */
  private async deleteMarked(): Promise<void> {
    const button = this.require<HTMLButtonElement>('.manage-delete');
    if (button.dataset.confirm !== 'true') {
      button.dataset.confirm = 'true';
      button.textContent = '再点一次确认';
      this.deleteConfirmTimer = window.setTimeout(() => {
        this.resetDeleteConfirm();
        this.refreshMarks();
      }, 3000);
      return;
    }
    this.resetDeleteConfirm();
    const ids = [...this.marked];
    button.disabled = true;
    try {
      if (!(await this.stage.settle())) {
        this.refreshMarks();
        return;
      }
      await this.bridge.request('records.delete', { ids });
    } catch (error) {
      this.refreshMarks();
      this.toast(error instanceof Error ? error.message : String(error), { kind: 'error' });
      return;
    }
    this.setManaging(false);
    await this.loadRecords();
    this.toast(`已把 ${ids.length} 条移入回收站。`, {
      undo: async () => {
        try {
          await this.bridge.request('record.undo', { id: ids[0] });
          await this.loadRecords();
          this.toast('这些记录找回来了。');
        } catch (error) {
          this.toast(error instanceof Error ? error.message : String(error), { kind: 'error' });
        }
      },
    });
  }

  /** 日历选到的那天可能被搜索过滤掉了，先清空搜索再跳。 */
  private async jumpToDay(day: string): Promise<void> {
    if (this.searchText) {
      this.searchText = '';
      this.require<HTMLInputElement>('#history-search').value = '';
      await this.loadRecords();
    }
    this.list.scrollToDate(day);
  }

  private afterSelection(): void {
    this.list.setSelected(this.highlighted);
    this.scene.setSelected(this.selected);
    this.openSelection();
    this.updateSummary();
    this.require('.stage-hint').dataset.hidden = String(this.selected.size > 0);
  }

  private openSelection(): void {
    // 按选择顺序，这也是复制时的编号顺序
    this.stage.show(this.resolve([...this.selected]));
  }

  private async copy(ids: string[], operation: CopyOperation): Promise<void> {
    const selected = this.resolve(ids);
    if (selected.length === 0) return;
    // 原生层按磁盘内容生成剪贴板，所以必须先等文字落盘
    if (!(await this.stage.settle())) {
      this.toast('文字还没保存成功，这次先不复制。', { kind: 'error' });
      return;
    }
    const latest = this.resolve(ids);
    const text = operation === 'ai' ? formatManyForAI(latest) : operation === 'text' ? joinTexts(latest) : operation === 'image-path' ? latest[0].imagePath : latest[0].markdownPath;
    if (operation === 'text' && !text) {
      this.toast(latest.length > 1 ? '选中的记录都还没有文字。' : '这条记录还没有文字。');
      return;
    }
    try {
      await this.bridge.request('clipboard.copy', { operation, ids: latest.map((record) => record.id), text });
      if (operation === 'ai') this.scene.celebrate();
      this.toast(operation === 'ai' ? `已复制 ${selected.length} 条，可以发给 AI 了。` : operation === 'text' ? this.textCopiedMessage(latest) : '已复制。');
    } catch (error) {
      this.toast(error instanceof Error ? error.message : String(error), { kind: 'error' });
    }
  }

  private textCopiedMessage(records: QuanShuoRecord[]): string {
    if (records.length === 1) return '已复制。';
    const filled = records.filter((record) => record.text.trim()).length;
    const skipped = records.length - filled;
    return `已复制 ${filled} 条文字${skipped > 0 ? `，${skipped} 条没有文字，跳过了` : ''}。`;
  }

  private resolve(ids: string[]): QuanShuoRecord[] {
    const byId = new Map(this.allRecords.map((record) => [record.id, record]));
    return ids.flatMap((id) => byId.get(id) ?? []);
  }

  private updateSummary(): void {
    const count = this.selected.size;
    const hasRecords = this.allRecords.length > 0;
    this.require('.selection-summary').textContent = count > 0 ? `已选 ${count} 条` : hasRecords ? '未选择时复制最新一条' : '';
    const button = this.require<HTMLButtonElement>('.copy-ai');
    button.disabled = !hasRecords;
    this.require<HTMLButtonElement>('.manage-button').disabled = !hasRecords && !this.managing;
  }

  private onMinute(): void {
    this.syncClock();
    // 过了零点，“今天”的记录要重新算，海棠也从淡墨重新开始
    const today = dayKey(new Date().toISOString());
    if (today === this.shownDay) return;
    this.shownDay = today;
    this.renderRecords();
  }

  private syncClock(): void {
    if (!this.followClock) return;
    const day = dayFromClock(new Date());
    this.scene.setDay(day);
    this.require<HTMLInputElement>('.day-slider').value = String(day);
  }

  private applySettings(settings: AppSettings): void {
    if ((settings.skin ?? 'starlake') !== this.options.skin) {
      this.switchSkin(settings);
      return;
    }
    this.scene.setQuiet(settings.quietMode || matchMedia('(prefers-reduced-motion: reduce)').matches);
    const fixed = settings.fixedDayPeriod !== undefined && settings.fixedDayPeriod !== null;
    this.followClock = !fixed;
    if (fixed) {
      const day = clampDay(settings.fixedDayPeriod!);
      this.scene.setDay(day);
      this.require<HTMLInputElement>('.day-slider').value = String(day);
    } else {
      this.syncClock();
    }
    this.applyHotkeys(settings);
  }

  /** 两套皮肤的画面完全不同，整页重开最干净：3D 湖占的显存也一起释放。文字在设置保存之前就落盘了。 */
  private switchSkin(settings: AppSettings): void {
    if (this.switchingSkin) return;
    this.switchingSkin = true;
    const resume: Resume = { search: this.searchText, selected: [...this.selected], marked: [...this.marked], managing: this.managing };
    try { sessionStorage.setItem(RESUME_KEY, JSON.stringify(resume)); } catch { /* 记不住就从头开始 */ }
    // 新页面只看地址，同一次保存里改的安静模式和时段要写进去
    const url = new URL(location.href);
    url.searchParams.set('skin', settings.skin ?? 'starlake');
    url.searchParams.set('state', 'normal');
    if (settings.quietMode) url.searchParams.set('quiet', '1');
    else url.searchParams.delete('quiet');
    if (settings.fixedDayPeriod === undefined || settings.fixedDayPeriod === null) url.searchParams.delete('day');
    else url.searchParams.set('day', String(settings.fixedDayPeriod));
    location.replace(url);
  }

  private takeResume(): Resume | null {
    try {
      const saved = sessionStorage.getItem(RESUME_KEY);
      sessionStorage.removeItem(RESUME_KEY);
      return saved ? JSON.parse(saved) as Resume : null;
    } catch {
      return null;
    }
  }

  private applyResume(resume: Resume): void {
    if (resume.managing) {
      this.setManaging(true);
      this.marked = new Set(this.resolve(resume.marked).map((record) => record.id));
      this.refreshMarks();
      return;
    }
    const selected = this.resolve(resume.selected).map((record) => record.id);
    if (selected.length === 0) return;
    this.selected = new Set(selected);
    this.afterSelection();
  }

  private applyHotkeys(settings: AppSettings): void {
    this.hotkey = settings.hotkey;
    this.speechHotkey = settings.speechHotkey;
    const keys = (hotkey: string) => hotkey.split('+').map((key) => `<kbd>${escapeHtml(key)}</kbd>`).join('');
    this.require('.stage-hint').innerHTML = `${keys(this.hotkey)}<span>圈选屏幕</span><span class="hint-gap"></span>${keys(this.speechHotkey)}<span>直接说话</span>`;
    this.require('.speech-button').title = `说话（${this.speechHotkey}）`;
  }

  private showStarTip(detail: { id: string; x: number; y: number } | null): void {
    const tip = this.require('.star-tip');
    const record = detail ? this.allRecords.find((candidate) => candidate.id === detail.id) : undefined;
    if (!detail || !record) {
      tip.hidden = true;
      return;
    }
    tip.innerHTML = `<time>${escapeHtml(formatTime(record.timestamp))}</time>${escapeHtml(record.text || '还没有说明')}`;
    tip.style.left = `${detail.x}px`;
    tip.style.top = `${detail.y}px`;
    tip.hidden = false;
  }

  private showListening(text: string): void {
    const panel = this.require('.listening');
    panel.hidden = false;
    this.require('.listening-text').textContent = text;
    this.keepListening();
  }

  /** 电平事件持续到来时保持显示；录音结束后自动收起。 */
  private keepListening(): void {
    window.clearTimeout(this.listeningTimer);
    if (this.options.state === 'recording') return;
    this.listeningTimer = window.setTimeout(() => this.hideListening(), 2500);
  }

  private hideListening(): void {
    this.require('.listening').hidden = true;
    this.scene.setMicrophoneLevel(0);
  }

  private toast(message: string, options: { undo?: () => Promise<void>; kind?: 'error' } = {}): void {
    const toast = this.require('.toast');
    window.clearTimeout(this.toastTimer);
    toast.dataset.kind = options.kind ?? '';
    toast.replaceChildren();
    toast.insertAdjacentHTML('beforeend', icon(options.kind === 'error' ? 'warning' : this.text.toastIcon));
    toast.append(element('span', undefined, message));
    if (options.undo) {
      const button = element('button', undefined, '撤销') as HTMLButtonElement;
      button.addEventListener('click', () => {
        toast.dataset.hidden = 'true';
        void options.undo!();
      });
      toast.append(button);
    }
    toast.dataset.hidden = 'false';
    this.toastTimer = window.setTimeout(() => { toast.dataset.hidden = 'true'; }, options.undo ? 8000 : 3200);
  }

  private isEditing(target: EventTarget | null): boolean {
    return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || (target instanceof HTMLElement && target.isContentEditable);
  }

  private template(): string {
    return `<div class="app">
      ${this.options.skin === 'crabapple'
        ? `<div class="garden" data-receded="false"><div class="roundel"><canvas class="bloom" role="img" aria-label="${this.text.sceneLabel}"></canvas></div></div><canvas class="petals" aria-hidden="true"></canvas>`
        : `<canvas class="lake" aria-label="${this.text.sceneLabel}"></canvas><img class="lake-fallback" alt="" hidden>`}
      <header class="titlebar">
        <div class="brand"><span class="brand-mark" aria-hidden="true"></span><span class="brand-name">圈说</span></div>
        <button class="icon-button journal-toggle">${icon('sidebar')}</button>
        <div class="titlebar-spacer"></div>
        <label class="day-control" title="拖动改变湖面的时间"><input class="day-slider" type="range" min="${DUSK}" max="${NIGHT}" step="0.01" aria-label="场景时段"><output></output></label>
        <button class="icon-button speech-button" aria-label="说话" title="说话">${icon('mic')}</button>
        <button class="icon-button settings-button" aria-label="设置" title="设置">${icon('sliders')}</button>
        <div class="window-controls">
          <button class="window-button minimize-button" aria-label="最小化">${icon('minimize')}</button>
          <button class="window-button maximize-button" aria-label="最大化">${icon('maximize')}</button>
          <button class="window-button close close-button" aria-label="关闭">${icon('close')}</button>
        </div>
      </header>
      <aside class="journal glass" aria-label="圈说历史">
        <div class="journal-head">
          <div class="today-date"><span class="today-day"></span><span class="head-actions"><button class="icon-button manage-button" aria-pressed="false" aria-label="批量选择：复制或删除" title="批量选择：复制或删除">${icon('checklist')}</button><button class="icon-button date-jump" aria-label="跳转到日期" aria-expanded="false" title="跳转到日期">${icon('calendar')}</button></span></div>
          <h1 class="today-title">${this.text.countBefore} <strong id="star-count">0</strong> ${this.text.countAfter}</h1>
        </div>
        <div class="search-wrap">${icon('search', 'search-icon')}<input class="text-input search-input" id="history-search" type="search" placeholder="找一句说过的话" aria-label="搜索历史记录"></div>
        <div class="active-date" data-empty="true"><span>今天</span></div>
        <div class="record-scroll" aria-label="历史记录列表"></div>
        <footer class="journal-foot"><span class="selection-summary"></span><button class="primary-button copy-ai">复制给 AI</button></footer>
        <footer class="manage-foot" aria-label="批量选择">
          <div class="manage-row"><button class="manage-pick manage-all">全选</button><button class="manage-pick manage-incomplete">未转完</button><span class="manage-count"></span><button class="manage-pick manage-cancel">取消</button></div>
          <div class="manage-row"><button class="primary-button manage-copy-ai" disabled>复制给 AI</button><button class="secondary-button manage-copy-text" disabled>只复制文字</button><button class="manage-delete" data-confirm="false" disabled>删除</button></div>
        </footer>
      </aside>
      <section class="calendar-pop" aria-label="跳转到日期" hidden></section>
      <section class="stage-figure" data-open="false" aria-label="选中的截图"></section>
      <section class="inspector glass" aria-label="记录详情" hidden></section>
      <div class="listening glass" role="status" hidden><div class="listening-head"><i></i><span>正在听</span></div><div class="listening-text"></div></div>
      <div class="stage-hint" data-hidden="false"></div>
      <div class="star-tip" hidden></div>
      <section class="settings-view" hidden></section>
      <section class="onboarding-view" hidden></section>
      <div class="toast" data-hidden="true" role="status"></div>
      <div class="debug-panel" data-hidden="true"></div>
    </div>`;
  }

  private require<T extends HTMLElement = HTMLElement>(selector: string): T {
    const node = this.root.querySelector<T>(selector);
    if (!node) throw new Error(`界面节点不存在：${selector}`);
    return node;
  }
}
