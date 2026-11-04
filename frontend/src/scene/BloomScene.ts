import type { QuanShuoRecord } from '../bridge/types';
import type { QueryOptions } from '../app/query';
import layout from '../config/crabapple-flowers.json';
import waterLayout from '../config/crabapple-flowers-blue.json';
import { dayKey } from '../utils/format';
import type { Scene } from './Scene';

interface Bloom { id: string; flower: number; bornAt: number }
/** 开满以后能换的两幅画：粉色是一天里一朵朵开出来的那幅，蓝色是水边那幅。 */
type Painting = 'pink' | 'blue';
interface Petal { x: number; y: number; vx: number; vy: number; sway: number; phase: number; spin: number; angle: number; size: number; colour: string; bornAt: number }

const BLOOM_SECONDS = 0.9;
const WHOLE_SECONDS = 1.6;
const SWAP_SECONDS = 0.9;
const PAINTING_KEY = 'quanshuo.crabapple.painting';
// 两幅画里同一条记录对应的花，位置不一样
const SPOTS: Record<Painting, { box: number[] }[]> = { pink: layout.flowers, blue: waterLayout.flowers };
const PETAL_COLOURS = ['#f3d3c8', '#e9b3aa', '#f8e6da', '#dd9a98'];
// 选中截图时的收起动画比换图短，等一下再放回来，切换记录时圆画才不会闪一下
const RETURN_DELAY = 200;

/**
 * 海棠的主画面：每天从淡墨稿开始，一条记录给一朵花上色，开满后整幅转彩，再有记录就飘花瓣。
 * 平时完全不跑动画循环，所以比 3D 湖省电得多。
 */
export class BloomScene extends EventTarget implements Scene {
  private context: CanvasRenderingContext2D;
  private petalContext: CanvasRenderingContext2D;
  private ink: HTMLImageElement | null = null;
  private colour: HTMLImageElement | null = null;
  private atlas: HTMLImageElement | null = null;
  private water: HTMLImageElement | null = null;
  private blooms: Bloom[] = [];
  private petals: Petal[] = [];
  private hovered: string | null = null;
  private selected = new Set<string>();
  private whole = 0;
  private wholeTarget = 0;
  private painting: Painting = readPainting();
  /** 0 是粉色那幅，1 是水边那幅，换的时候在中间渐变。 */
  private blue = 0;
  private readonly label: string;
  private frame = 0;
  private lastFrame = 0;
  private quiet: boolean;
  private readonly fixedTime: boolean;
  private returnTimer = 0;
  private breezeTimer = 0;
  private ready = false;

  constructor(private canvas: HTMLCanvasElement, private petalCanvas: HTMLCanvasElement, options: QueryOptions) {
    super();
    this.context = canvas.getContext('2d')!;
    this.petalContext = petalCanvas.getContext('2d')!;
    this.quiet = options.quiet;
    this.fixedTime = new URLSearchParams(location.search).has('t');
    this.label = canvas.getAttribute('aria-label') ?? '';
    document.documentElement.dataset.quiet = String(this.quiet);
    // 海棠只有一种样子，时段固定下来，配色不再跟着时间走
    document.documentElement.dataset.period = 'noon';
    if (options.wind !== undefined) this.setForcedVoice(options.wind);
    canvas.addEventListener('pointermove', (event) => this.onPointerMove(event), { passive: true });
    canvas.addEventListener('pointerleave', () => this.setSceneHover(null, 0, 0));
    canvas.addEventListener('click', (event) => this.onClick(event));
    new ResizeObserver(() => this.resize()).observe(canvas);
    window.addEventListener('resize', () => this.resizePetals());
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.stop();
      else this.start();
    });
  }

  async initialize(): Promise<void> {
    // 水边那幅只在开满后才用得上，读不出来也只是不能换
    const water = loadImage('./assets/crabapple/colour-blue.webp').catch((error) => { console.warn('水边那幅画没有加载：', error); return null; });
    try {
      [this.ink, this.colour, this.atlas] = await Promise.all(['ink', 'colour', 'flowers'].map((name) => loadImage(`./assets/crabapple/${name}.webp`)));
      this.ready = true;
    } catch (error) {
      // 画读不出来也不影响记录，圆框留空
      console.warn('海棠的画没有加载：', error);
    }
    this.water = await water;
    this.blue = this.blueTarget;
    this.syncPainting();
    this.resize();
    this.resizePetals();
    // 画加载完之前画布已经定好尺寸，resize 不会再画，这里补一次。加载期间到的新记录也接着动起来
    this.draw();
    this.start();
    this.scheduleBreeze();
    document.body.dataset.sceneReady = 'true';
  }

  setRecords(records: QuanShuoRecord[], animateNew: boolean): void {
    const today = dayKey(new Date().toISOString());
    const todayRecords = records.filter((record) => dayKey(record.timestamp) === today).sort((a, b) => a.timestamp.localeCompare(b.timestamp));
    const known = new Map(this.blooms.map((bloom) => [bloom.id, bloom.bornAt]));
    const now = performance.now() / 1000;
    const animate = animateNew && this.moving;
    const newcomers = todayRecords.filter((record) => !known.has(record.id)).length;
    this.blooms = todayRecords.slice(0, layout.flowers.length).map((record, flower) => ({
      id: record.id,
      flower,
      bornAt: known.get(record.id) ?? (animate ? now : -100),
    }));
    this.wholeTarget = todayRecords.length >= layout.flowers.length ? 1 : 0;
    if (!animate) {
      this.whole = this.wholeTarget;
      this.blue = this.blueTarget;
    }
    this.syncPainting();
    // 花都开满了，新记录没有花可开，飘几片花瓣告诉用户记下了
    if (animate && todayRecords.length > layout.flowers.length) this.dropPetals(Math.min(6, newcomers * 3));
    this.draw();
    this.start();
  }

  /** 海棠不随时间变化。 */
  setDay(): void { /* 只有一种样子 */ }

  setMicrophoneLevel(level: number): void {
    if (!this.quiet) this.canvas.parentElement?.style.setProperty('--voice', Math.max(0, Math.min(1, level)).toFixed(3));
  }

  setForcedVoice(level: number): void {
    this.canvas.parentElement?.style.setProperty('--voice', level.toFixed(3));
  }

  setQuiet(quiet: boolean): void {
    this.quiet = quiet;
    document.documentElement.dataset.quiet = String(quiet);
    if (quiet) {
      this.finishAnimations();
      this.petals = [];
      this.clearPetals();
      this.canvas.parentElement?.style.setProperty('--voice', '0');
    }
    this.scheduleBreeze();
    this.draw();
  }

  setHovered(id: string | null): void {
    this.hovered = id;
    this.draw();
  }

  setSelected(ids: Set<string>): void {
    this.selected = ids;
    this.draw();
  }

  celebrate(): void {
    this.dropPetals(9);
  }

  /** 舞台上有截图时圆画退成淡淡的水印，让人专心看图和字。 */
  setStageImage(image: TexImageSource | null): void {
    const garden = this.canvas.parentElement?.parentElement;
    if (!garden) return;
    window.clearTimeout(this.returnTimer);
    if (image) garden.dataset.receded = 'true';
    else this.returnTimer = window.setTimeout(() => { garden.dataset.receded = 'false'; }, RETURN_DELAY);
  }

  updateStageRect(): void { /* 海棠没有倒影 */ }

  private get moving(): boolean {
    return !this.quiet && !this.fixedTime;
  }

  private finishAnimations(): void {
    this.blooms.forEach((bloom) => { bloom.bornAt = -100; });
    this.whole = this.wholeTarget;
    this.blue = this.blueTarget;
  }

  /** 花开满了才能换画；没开满时点空白处什么也不做。 */
  private get canSwap(): boolean {
    return this.ready && this.water !== null && this.wholeTarget === 1;
  }

  /** 选了水边那幅时，等粉色那幅整幅转彩以后再渐变过去，看得出是今天的花开满了。 */
  private get blueTarget(): number {
    return this.painting === 'blue' && this.water !== null && this.whole >= 1 ? 1 : 0;
  }

  /** 现在看到的是哪一幅：移到花上、点花都按这幅的位置找。 */
  private get spots(): { box: number[] }[] {
    return this.blue > 0.5 ? SPOTS.blue : SPOTS.pink;
  }

  private get animating(): boolean {
    const now = performance.now() / 1000;
    return this.petals.length > 0 || this.whole !== this.wholeTarget || this.blue !== this.blueTarget || this.blooms.some((bloom) => now - bloom.bornAt < BLOOM_SECONDS);
  }

  private start(): void {
    if (this.frame || !this.ready || document.hidden || !this.animating) return;
    this.lastFrame = performance.now();
    this.frame = requestAnimationFrame((time) => this.tick(time));
  }

  private stop(): void {
    if (this.frame) cancelAnimationFrame(this.frame);
    this.frame = 0;
  }

  private tick(time: number): void {
    this.frame = 0;
    const delta = Math.min(0.05, (time - this.lastFrame) / 1000);
    this.lastFrame = time;
    if (this.whole !== this.wholeTarget) {
      const step = delta / WHOLE_SECONDS;
      this.whole = this.wholeTarget > this.whole ? Math.min(this.wholeTarget, this.whole + step) : Math.max(this.wholeTarget, this.whole - step);
    }
    const blueTarget = this.blueTarget;
    if (this.blue !== blueTarget) {
      const step = delta / SWAP_SECONDS;
      this.blue = blueTarget > this.blue ? Math.min(blueTarget, this.blue + step) : Math.max(blueTarget, this.blue - step);
    }
    this.draw();
    this.advancePetals(delta);
    if (this.animating) this.frame = requestAnimationFrame((next) => this.tick(next));
    else this.clearPetals();
  }

  private resize(): void {
    const box = this.canvas.getBoundingClientRect();
    if (box.width <= 0) return;
    const scale = Math.min(devicePixelRatio || 1, layout.size / box.width);
    const size = Math.max(1, Math.round(box.width * Math.max(1, scale)));
    if (this.canvas.width === size) return;
    this.canvas.width = this.canvas.height = size;
    this.draw();
  }

  private resizePetals(): void {
    const ratio = devicePixelRatio || 1;
    this.petalCanvas.width = Math.round(innerWidth * ratio);
    this.petalCanvas.height = Math.round(innerHeight * ratio);
    this.petalContext.setTransform(ratio, 0, 0, ratio, 0, 0);
  }

  private draw(): void {
    if (!this.ready || !this.ink || !this.colour || !this.atlas) return;
    const context = this.context;
    const size = this.canvas.width;
    const scale = size / layout.size;
    const now = performance.now() / 1000;
    context.globalAlpha = 1;
    context.drawImage(this.ink, 0, 0, size, size);
    this.blooms.forEach((bloom) => {
      const flower = layout.flowers[bloom.flower];
      const progress = Math.min(1, Math.max(0, (now - bloom.bornAt) / BLOOM_SECONDS));
      const eased = 1 - (1 - progress) ** 3;
      const grow = 0.9 + 0.1 * eased;
      const [x, y, width, height] = flower.box;
      const [atlasX, atlasY] = flower.at;
      const drawWidth = width * scale * grow, drawHeight = height * scale * grow;
      context.globalAlpha = eased;
      context.drawImage(this.atlas!, atlasX, atlasY, width, height,
        (x + width / 2) * scale - drawWidth / 2, (y + height / 2) * scale - drawHeight / 2, drawWidth, drawHeight);
    });
    if (this.whole > 0) {
      context.globalAlpha = this.whole;
      context.drawImage(this.colour, 0, 0, size, size);
    }
    if (this.water && this.blue > 0) {
      context.globalAlpha = this.blue * this.whole;
      context.drawImage(this.water, 0, 0, size, size);
    }
    context.globalAlpha = 1;
    const spots = this.spots;
    this.blooms.forEach((bloom) => {
      if (bloom.id !== this.hovered && !this.selected.has(bloom.id)) return;
      const box = spots[bloom.flower]?.box;
      if (!box) return;
      const cx = (box[0] + box[2] / 2) * scale, cy = (box[1] + box[3] / 2) * scale;
      const radius = (Math.max(box[2], box[3]) / 2 + 4) * scale;
      context.lineWidth = Math.max(1.5, 5 * scale);
      context.strokeStyle = 'rgba(191, 148, 64, .28)';
      context.beginPath();
      context.arc(cx, cy, radius + context.lineWidth, 0, Math.PI * 2);
      context.stroke();
      context.lineWidth = Math.max(1.25, 2.5 * scale);
      context.strokeStyle = '#a97f2c';
      context.beginPath();
      context.arc(cx, cy, radius, 0, Math.PI * 2);
      context.stroke();
    });
  }

  private flowerAt(event: PointerEvent | MouseEvent): Bloom | null {
    const box = this.canvas.getBoundingClientRect();
    const x = (event.clientX - box.left) / box.width * layout.size;
    const y = (event.clientY - box.top) / box.height * layout.size;
    let best: Bloom | null = null;
    let bestDistance = Infinity;
    const spots = this.spots;
    this.blooms.forEach((bloom) => {
      if (!spots[bloom.flower]) return;
      const [left, top, width, height] = spots[bloom.flower].box;
      const distance = Math.hypot(left + width / 2 - x, top + height / 2 - y);
      if (distance < Math.max(width, height) / 2 + 6 && distance < bestDistance) { best = bloom; bestDistance = distance; }
    });
    return best;
  }

  private onPointerMove(event: PointerEvent): void {
    this.setSceneHover(this.flowerAt(event)?.id ?? null, event.clientX, event.clientY);
  }

  private setSceneHover(id: string | null, x: number, y: number): void {
    if (id === this.hovered && id === null) return;
    this.canvas.style.cursor = id ? 'pointer' : '';
    this.hovered = id;
    this.draw();
    this.dispatchEvent(new CustomEvent('starhover', { detail: id ? { id, x, y } : null }));
  }

  private onClick(event: MouseEvent): void {
    const bloom = this.flowerAt(event);
    if (bloom) this.dispatchEvent(new CustomEvent('starselect', { detail: bloom.id }));
    else if (this.canSwap) this.swapPainting();
  }

  /** 花开满以后点空白处，在粉色那幅和水边那幅之间换，记住选的是哪幅。 */
  private swapPainting(): void {
    this.painting = this.painting === 'blue' ? 'pink' : 'blue';
    try { localStorage.setItem(PAINTING_KEY, this.painting); } catch { /* 记不住的话下次回到粉色 */ }
    if (!this.moving) this.blue = this.blueTarget;
    this.syncPainting();
    this.draw();
    this.start();
  }

  /** 能不能换、正在看哪幅，写在画布上：能换时鼠标是手形，读屏也知道可以点。 */
  private syncPainting(): void {
    const swappable = this.canSwap;
    this.canvas.dataset.swappable = String(swappable);
    this.canvas.dataset.painting = this.painting === 'blue' && swappable ? 'blue' : 'pink';
    this.canvas.setAttribute('aria-label', swappable ? `${this.label}。花开满了，点空白处换一幅画` : this.label);
  }

  private scheduleBreeze(): void {
    window.clearTimeout(this.breezeTimer);
    if (!this.moving) return;
    this.breezeTimer = window.setTimeout(() => {
      if (!document.hidden) this.dropPetals(1);
      this.scheduleBreeze();
    }, 16_000 + Math.random() * 18_000);
  }

  private dropPetals(count: number): void {
    if (!this.moving || !this.ready) return;
    const box = this.canvas.getBoundingClientRect();
    const now = performance.now() / 1000;
    for (let index = 0; index < count; index++) {
      this.petals.push({
        x: box.left + box.width * (0.15 + Math.random() * 0.7),
        y: box.top + box.height * (0.05 + Math.random() * 0.3) - index * 26,
        vx: 14 + Math.random() * 22,
        vy: 38 + Math.random() * 30,
        sway: 14 + Math.random() * 20,
        phase: Math.random() * Math.PI * 2,
        spin: (Math.random() - 0.5) * 1.8,
        angle: Math.random() * Math.PI * 2,
        size: 7 + Math.random() * 6,
        colour: PETAL_COLOURS[index % PETAL_COLOURS.length],
        bornAt: now,
      });
    }
    this.start();
  }

  private advancePetals(delta: number): void {
    const context = this.petalContext;
    context.clearRect(0, 0, innerWidth, innerHeight);
    const now = performance.now() / 1000;
    this.petals = this.petals.filter((petal) => petal.y < innerHeight + 30);
    this.petals.forEach((petal) => {
      const age = now - petal.bornAt;
      petal.x += (petal.vx + Math.sin(age * 1.3 + petal.phase) * petal.sway) * delta;
      petal.y += petal.vy * delta;
      petal.angle += petal.spin * delta;
      const fade = Math.min(1, age * 2.5) * Math.min(1, (innerHeight + 30 - petal.y) / 160);
      // 花瓣绕长轴翻转，看起来忽宽忽窄
      const turn = 0.45 + 0.55 * Math.abs(Math.cos(age * 1.1 + petal.phase));
      context.save();
      context.translate(petal.x, petal.y);
      context.rotate(petal.angle);
      context.scale(1, turn);
      context.globalAlpha = fade * 0.92;
      context.fillStyle = petal.colour;
      context.beginPath();
      context.moveTo(-petal.size, 0);
      context.bezierCurveTo(-petal.size * 0.4, -petal.size * 0.78, petal.size * 0.72, -petal.size * 0.56, petal.size, 0);
      context.bezierCurveTo(petal.size * 0.72, petal.size * 0.56, -petal.size * 0.4, petal.size * 0.78, -petal.size, 0);
      context.fill();
      context.strokeStyle = 'rgba(150, 70, 70, .28)';
      context.lineWidth = 0.6;
      context.stroke();
      context.restore();
    });
  }

  private clearPetals(): void {
    this.petalContext.clearRect(0, 0, innerWidth, innerHeight);
  }
}

function readPainting(): Painting {
  try { return localStorage.getItem(PAINTING_KEY) === 'blue' ? 'blue' : 'pink'; } catch { return 'pink'; }
}

function loadImage(url: string): Promise<HTMLImageElement> {
  const image = new Image();
  image.src = url;
  return image.decode().then(() => image);
}
