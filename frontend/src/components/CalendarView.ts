import type { QuanShuoRecord } from '../bridge/types';
import type { Skin } from '../app/skin';
import { dayKey } from '../utils/format';
import { icon } from './icons';

const WEEKDAYS = ['一', '二', '三', '四', '五', '六', '日'];
const MONTHS = ['一月', '二月', '三月', '四月', '五月', '六月', '七月', '八月', '九月', '十月', '十一月', '十二月'];
const CELLS = 42;

// 星图的尺寸。星星不排在格子正中：每个月同一片天，但连线不会和网格对齐
const CELL_WIDTH = 48;
const CELL_HEIGHT = 50;
const STAR_TOP = 16;
const DRIFT_X = 22;
const DRIFT_Y = 11;
const SKY_WIDTH = CELL_WIDTH * 7;
const STAR_SIZE = [0, 3.4, 4.6, 6, 7.6];
const STAR_GLOW = [0, 10, 14, 19, 26];
const DUST = 90;
/** 比这更远的两颗星不连线，横穿整个日历的长线不像星座。 */
const LINK_REACH = CELL_WIDTH * 2.35;
const RING_X = 330;
const RING_Y = -60;
const RING_OUTER = 268;
const RING_INNER = 196;
const RING_TICKS = 96;
const SYNODIC_MONTH = 29.530588;
const NEW_MOON = Date.UTC(2000, 0, 6, 18, 14);

/** 这一天的记录有多少，分四档。海棠从花苞开到满枝，星湖越来越亮。 */
function levelFor(count: number): number {
  if (count >= 10) return 4;
  if (count >= 4) return 3;
  if (count >= 2) return 2;
  return 1;
}

const FLOWER_STAGES = ['bud', 'half', 'bloom', 'cluster'];

function seeded(seed: number): () => number {
  let state = seed;
  return () => {
    state = (state * 16807) % 2147483647;
    return state / 2147483647;
  };
}

/** 四芒星，四条边向中心收成弧线。 */
function sparkle(radius: number): string {
  const k = radius * 0.1;
  return `M0 ${-radius}Q${k} ${-k} ${radius} 0Q${k} ${k} 0 ${radius}Q${-k} ${k} ${-radius} 0Q${-k} ${-k} 0 ${-radius}Z`;
}

/** 这天真实的月相，用平均朔望月算。 */
function moonPath(when: Date, radius: number, center: number): string {
  const age = (((when.getTime() - NEW_MOON) / 864e5) % SYNODIC_MONTH + SYNODIC_MONTH) % SYNODIC_MONTH;
  const phase = age / SYNODIC_MONTH;
  const wave = Math.cos(phase * 2 * Math.PI);
  const waxing = phase < 0.5;
  const top = center - radius, bottom = center + radius;
  return `M${center} ${top}A${radius} ${radius} 0 0 ${waxing ? 1 : 0} ${center} ${bottom}A${Math.abs(wave) * radius} ${radius} 0 0 ${(wave > 0) === waxing ? 0 : 1} ${center} ${top}Z`;
}

/** 自绘的月历。系统日期选择器改不了样式，也标不出哪天有记录。 */
export class CalendarView extends EventTarget {
  private counts = new Map<string, number>();
  private month = new Date();

  constructor(private root: HTMLElement, private trigger: HTMLElement, private skin: Skin) {
    super();
    root.addEventListener('click', (event) => this.onClick(event));
    document.addEventListener('pointerdown', (event) => {
      if (!this.isOpen) return;
      const target = event.target as Node;
      if (!root.contains(target) && !trigger.contains(target)) this.hide();
    });
  }

  get isOpen(): boolean {
    return !this.root.hidden;
  }

  setRecords(records: QuanShuoRecord[]): void {
    this.counts = new Map();
    records.forEach((record) => {
      const day = dayKey(record.timestamp);
      this.counts.set(day, (this.counts.get(day) ?? 0) + 1);
    });
    if (this.isOpen) this.render();
  }

  toggle(): void {
    if (this.isOpen) this.hide();
    else this.show();
  }

  show(): void {
    this.month = new Date();
    this.render();
    this.root.hidden = false;
    this.trigger.setAttribute('aria-expanded', 'true');
  }

  hide(): void {
    this.root.hidden = true;
    this.trigger.setAttribute('aria-expanded', 'false');
  }

  private onClick(event: Event): void {
    const target = (event.target as HTMLElement).closest<HTMLButtonElement>('button');
    if (!target) return;
    if (target.dataset.step) {
      this.month = new Date(this.month.getFullYear(), this.month.getMonth() + Number(target.dataset.step), 1);
      this.render();
    } else if (target.dataset.action === 'today') {
      this.month = new Date();
      this.render();
    } else if (target.dataset.day) {
      this.hide();
      this.dispatchEvent(new CustomEvent('pick', { detail: target.dataset.day }));
    }
  }

  private render(): void {
    if (this.skin === 'crabapple') this.renderSheet();
    else this.renderSky();
  }

  /** 海棠：花框里的月历。 */
  private renderSheet(): void {
    const year = this.month.getFullYear(), month = this.month.getMonth();
    const today = dayKey(new Date().toISOString());
    // 周一开头：getDay 周日是 0，要排到最后
    const lead = (new Date(year, month, 1).getDay() + 6) % 7;
    let total = 0;
    const cells: string[] = [];
    for (let index = 0; index < CELLS; index++) {
      const date = new Date(year, month, index - lead + 1);
      const key = dayKey(date.toISOString());
      const count = this.counts.get(key) ?? 0;
      const inMonth = date.getMonth() === month;
      if (inMonth) total += count;
      cells.push(this.cell(date, key, count, inMonth, key === today));
    }
    this.root.innerHTML = `<div class="cal-sheet">
      <header class="cal-plaque">
        <button class="cal-nav" data-step="-1" aria-label="上个月">${icon('back')}</button>
        <h2 data-month="${year} 年 ${month + 1} 月">${year} 年 ${month + 1} 月</h2>
        <button class="cal-nav next" data-step="1" aria-label="下个月">${icon('back')}</button>
      </header>
      <div class="cal-week" aria-hidden="true">${WEEKDAYS.map((day) => `<span>${day}</span>`).join('')}</div>
      <div class="cal-grid">${cells.join('')}</div>
      <footer class="cal-foot"><span>${total > 0 ? `这个月有 ${total} 条` : '这个月没有记录'}</span><button class="cal-today" data-action="today">回到今天</button></footer>
    </div>`;
  }

  private cell(date: Date, key: string, count: number, inMonth: boolean, isToday: boolean): string {
    const label = `${date.getMonth() + 1} 月 ${date.getDate()} 日`;
    const level = levelFor(count);
    const mark = count === 0 ? '' : `<img src="./assets/crabapple/mark-${FLOWER_STAGES[level - 1]}.svg" alt="">`;
    const attributes = count > 0
      ? `data-day="${key}" title="${label}，${count} 条" aria-label="${label}，${count} 条"`
      : `disabled aria-label="${label}，没有记录"`;
    return `<button class="cal-day" data-out="${!inMonth}" data-today="${isToday}" ${attributes}>${mark}<span>${date.getDate()}</span></button>`;
  }

  /** 星湖：一片天。有记录的日子是星，离得近的连成星座，记录越多星越亮。 */
  private renderSky(): void {
    const year = this.month.getFullYear(), month = this.month.getMonth();
    const days = new Date(year, month + 1, 0).getDate();
    const lead = (new Date(year, month, 1).getDay() + 6) % 7;
    const rows = Math.ceil((lead + days) / 7);
    const now = new Date();
    const today = dayKey(now.toISOString());
    const random = seeded(year * 37 + (month + 1) * 101);
    const drift = Array.from({ length: days + 1 }, () => [(random() - 0.5) * DRIFT_X, (random() - 0.5) * DRIFT_Y]);
    const place = (day: number) => {
      const index = lead + day - 1;
      const column = index % 7, row = Math.floor(index / 7);
      return { column, row, x: column * CELL_WIDTH + CELL_WIDTH / 2 + drift[day][0], y: row * CELL_HEIGHT + STAR_TOP + drift[day][1] };
    };
    const countOf = (day: number) => this.counts.get(dayKey(new Date(year, month, day).toISOString())) ?? 0;
    const lit = Array.from({ length: days }, (_, index) => index + 1).filter((day) => countOf(day) > 0);

    // 每颗星连向离它最近的、更早的星，并查集用来数连成了几个星座
    const parent = new Map(lit.map((day) => [day, day]));
    const find = (day: number): number => (parent.get(day) === day ? day : find(parent.get(day)!));
    const links: Array<[number, number]> = [];
    lit.forEach((day, index) => {
      const here = place(day);
      let nearest: number | null = null, distance = Infinity;
      for (const earlier of lit.slice(0, index)) {
        const there = place(earlier);
        const gap = Math.hypot(here.x - there.x, here.y - there.y);
        if (gap < distance) { distance = gap; nearest = earlier; }
      }

      if (nearest === null || distance > LINK_REACH) return;
      links.push([nearest, day]);
      parent.set(find(day), find(nearest));
    });
    const groups = new Map<number, number>();
    lit.forEach((day) => groups.set(find(day), (groups.get(find(day)) ?? 0) + 1));
    const constellations = [...groups.values()].filter((size) => size > 1).length;

    const height = rows * CELL_HEIGHT;
    let sky = '';
    for (let index = 0; index < DUST; index++) {
      sky += `<circle cx="${(random() * SKY_WIDTH).toFixed(1)}" cy="${(random() * height).toFixed(1)}" r="${(0.35 + random() * 0.55).toFixed(2)}" fill="#dfe8ff" opacity="${(0.08 + random() * 0.22).toFixed(2)}"/>`;
    }

    for (const [from, to] of links) {
      const start = place(from), end = place(to);
      const length = Math.hypot(end.x - start.x, end.y - start.y) || 1;
      const ux = (end.x - start.x) / length, uy = (end.y - start.y) / length;
      const startGap = STAR_SIZE[levelFor(countOf(from))] * 0.8, endGap = STAR_SIZE[levelFor(countOf(to))] * 0.8;
      sky += `<line x1="${start.x + ux * startGap}" y1="${start.y + uy * startGap}" x2="${end.x - ux * endGap}" y2="${end.y - uy * endGap}" stroke="url(#sky-line)"/>`;
    }

    const blanks = Array.from({ length: lead }, () => '<span class="sky-blank"></span>');
    const buttons: string[] = [];
    for (let day = 1; day <= days; day++) {
      const here = place(day);
      const key = dayKey(new Date(year, month, day).toISOString());
      const count = countOf(day);
      if (count === 0) sky += `<circle cx="${here.x}" cy="${here.y}" r="1.1" fill="#cdd9ff" opacity=".34"/>`;
      buttons.push(this.skyDay(day, month, key, count, key === today, here.x - here.column * CELL_WIDTH, here.y - here.row * CELL_HEIGHT));
    }

    const total = lit.reduce((sum, day) => sum + countOf(day), 0);
    const summary = total === 0 ? '这个月没有记录' : constellations > 0 ? `本月 ${total} 条，连成 ${constellations} 个星座` : `本月 ${total} 条`;
    const isThisMonth = year === now.getFullYear() && month === now.getMonth();
    const moonDate = isThisMonth ? now : new Date(year, month, 15, 12);
    const ticks = Array.from({ length: RING_TICKS }, (_, index) => {
      const angle = (index / RING_TICKS) * Math.PI * 2;
      const end = RING_OUTER + (index % 8 === 0 ? 10 : 5);
      return `<line x1="${RING_X + Math.cos(angle) * RING_OUTER}" y1="${RING_Y + Math.sin(angle) * RING_OUTER}" x2="${RING_X + Math.cos(angle) * end}" y2="${RING_Y + Math.sin(angle) * end}" stroke="#cfdcff" stroke-opacity=".13"/>`;
    }).join('');

    this.root.innerHTML = `<div class="sky-sheet">
      <svg class="sky-ring" aria-hidden="true"><circle cx="${RING_X}" cy="${RING_Y}" r="${RING_OUTER}" fill="none" stroke="#cfdcff" stroke-opacity=".11"/><circle cx="${RING_X}" cy="${RING_Y}" r="${RING_INNER}" fill="none" stroke="#cfdcff" stroke-opacity=".07"/>${ticks}</svg>
      <header class="sky-head">
        <h2 data-month="${year} 年 ${month + 1} 月" aria-label="${year} 年 ${month + 1} 月"><span class="sky-month">${MONTHS[month]}</span><span class="sky-year">${year}</span></h2>
        <div class="sky-tools">
          <svg class="sky-moon" width="26" height="26" aria-hidden="true"><circle cx="13" cy="13" r="11" fill="#242c52"/><path d="${moonPath(moonDate, 11, 13)}" fill="#efe6cf"/></svg>
          <button class="cal-nav" data-step="-1" aria-label="上个月">‹</button>
          <button class="cal-nav" data-step="1" aria-label="下个月">›</button>
        </div>
      </header>
      <div class="sky-week" aria-hidden="true">${WEEKDAYS.map((day) => `<span>${day}</span>`).join('')}</div>
      <div class="sky-grid" style="height:${height}px">
        <svg class="sky-field" width="${SKY_WIDTH}" height="${height}" aria-hidden="true"><defs><linearGradient id="sky-line" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="${SKY_WIDTH}" y2="300"><stop offset="0" stop-color="#d9efff" stop-opacity=".62"/><stop offset="1" stop-color="#bcd0ff" stop-opacity=".46"/></linearGradient></defs>${sky}</svg>
        ${blanks.join('')}${buttons.join('')}
      </div>
      <footer class="sky-foot"><span>${summary}</span><button class="cal-today" data-action="today">回到今天</button></footer>
    </div>`;
  }

  private skyDay(day: number, month: number, key: string, count: number, isToday: boolean, x: number, y: number): string {
    const label = `${month + 1} 月 ${day} 日`;
    const position = `--star-x:${x.toFixed(1)}px;--star-y:${y.toFixed(1)}px`;
    if (count === 0) return `<button class="cal-day" data-today="${isToday}" style="${position}" disabled aria-label="${label}，没有记录"><span>${day}</span></button>`;
    const level = levelFor(count);
    const radius = STAR_SIZE[level] * 1.5, box = STAR_GLOW[level] * 2;
    const star = `<i class="cal-star" data-level="${level}"><svg width="${box}" height="${box}" viewBox="${-box / 2} ${-box / 2} ${box} ${box}" aria-hidden="true"><path d="${sparkle(radius)}"/><circle r="${(STAR_SIZE[level] * 0.2).toFixed(2)}"/></svg></i>`;
    return `<button class="cal-day" data-day="${key}" data-today="${isToday}" style="${position}" title="${label}，${count} 条" aria-label="${label}，${count} 条">${star}<span>${day}</span></button>`;
  }
}
