import type { Skin } from './skin';

export type ViewState = 'normal' | 'empty' | 'loading' | 'hover' | 'selected' | 'recording' | 'failed' | 'search-empty' | 'detail' | 'settings' | 'onboarding';

export interface QueryOptions {
  state: ViewState;
  skin: Skin;
  records: number;
  day: number;
  /** URL 没有固定时段时，场景跟随系统时间。 */
  followClock: boolean;
  time: number;
  seed: number;
  quiet: boolean;
  webgl: boolean;
  wind?: number;
  debug: boolean;
  mask: boolean;
}

export function readQuery(query: URLSearchParams): QueryOptions {
  const allowedStates = new Set<ViewState>(['normal', 'empty', 'loading', 'hover', 'selected', 'recording', 'failed', 'search-empty', 'detail', 'settings', 'onboarding']);
  const requested = (query.get('state') ?? 'normal') as ViewState;
  return {
    state: allowedStates.has(requested) ? requested : 'normal',
    skin: query.get('skin') === 'crabapple' ? 'crabapple' : 'starlake',
    records: Math.max(0, Math.min(5000, Number(query.get('records') ?? 5) || 0)),
    day: query.has('day') ? clampDay(Number(query.get('day')) || 0) : dayFromClock(new Date()),
    followClock: !query.has('day'),
    time: Math.max(0, Number(query.get('t') ?? 0) || 0),
    seed: Number(query.get('seed') ?? 1) || 1,
    quiet: query.get('quiet') === '1' || matchMedia('(prefers-reduced-motion: reduce)').matches,
    webgl: query.get('webgl') !== '0',
    wind: query.has('wind') ? Number(query.get('wind')) : undefined,
    debug: query.get('debug') === '1',
    mask: query.get('mask') === '1',
  };
}

export const DUSK = 2;
export const NIGHT = 3;

/** 星湖只有傍晚和夜晚，旧设置里的清晨和正午都落到傍晚。 */
export function clampDay(day: number): number {
  return Math.max(DUSK, Math.min(NIGHT, day));
}

// [小时, 时段值]。早 6 点到晚 6 点是傍晚，晚 7 点到早 5 点是夜晚，中间各过渡一小时。
// 原生窗口要用同一条规则：NativeTheme.IsDusk。
const CLOCK_KEYS: Array<[number, number]> = [[0, NIGHT], [5, NIGHT], [6, DUSK], [18, DUSK], [19, NIGHT], [24, NIGHT]];

export function dayFromClock(date: Date): number {
  const hour = date.getHours() + date.getMinutes() / 60;
  for (let index = 1; index < CLOCK_KEYS.length; index++) {
    const [endHour, endDay] = CLOCK_KEYS[index];
    if (hour > endHour) continue;
    const [startHour, startDay] = CLOCK_KEYS[index - 1];
    return startDay + (endDay - startDay) * (hour - startHour) / (endHour - startHour);
  }
  return NIGHT;
}
