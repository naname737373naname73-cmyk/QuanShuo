import type { QuanShuoRecord } from '../bridge/types';
import type { QueryOptions } from '../app/query';
import config from '../config/scene.config.json';
import { dayKey } from '../utils/format';
import { SceneClock } from './Clock';
import { ConstellationLayer } from './ConstellationLayer';
import { LakeScene } from './LakeScene';
import type { Scene } from './Scene';

export interface SceneMetrics { fps: number; frameMs: number; voice: number; day: number; scale: number; renderer: string }

const PERIOD_IDS = config.periods.map((period) => period.id);

/** 调度湖景、星座和交互。业务层只跟这个类打交道。 */
export class SceneRenderer extends EventTarget implements Scene {
  readonly clock: SceneClock;
  private lake: LakeScene | null = null;
  private constellation = new ConstellationLayer();
  private frameHandle = 0;
  private lastFrame = 0;
  private currentDay: number;
  private targetDay: number;
  private voice = 0;
  private microphone = 0;
  private forcedVoice: number | null;
  private parallax = 0;
  private parallaxTarget = 0;
  private quiet: boolean;
  private initialized = false;
  private overlayDirty = true;
  private lastInteraction = 0;
  private lastVoiceRipple = 0;
  private lastPointerRipple = 0;
  private stage: { rect: [number, number, number, number]; alpha: number; target: number } | null = null;
  private metrics: SceneMetrics;
  private slowFrames = 0;
  private resolutionScale = 1;
  private hovered: string | null = null;
  private fallbackCanvas: HTMLCanvasElement | null = null;
  private fallbackBound = false;

  constructor(
    private canvas: HTMLCanvasElement,
    private fallback: HTMLImageElement,
    private options: QueryOptions,
  ) {
    super();
    this.clock = new SceneClock(options.time, new URLSearchParams(location.search).has('t'));
    this.currentDay = this.targetDay = options.day;
    this.forcedVoice = options.wind ?? null;
    this.quiet = options.quiet;
    this.metrics = { fps: 0, frameMs: 0, voice: 0, day: options.day, scale: 1, renderer: '正在启动' };
    this.applyPeriod(options.day);
    document.documentElement.dataset.quiet = String(this.quiet);
    window.addEventListener('resize', () => this.resize());
    window.addEventListener('blur', () => this.renderOnce());
    this.bindPointer(canvas);
    // 显卡驱动重置时上下文会丢失。不处理的话窗口直接黑掉，历史也用不了。
    canvas.addEventListener('webglcontextlost', (event) => {
      event.preventDefault();
      this.stop();
      this.stage = null;
      this.showFallback();
    });
    canvas.addEventListener('webglcontextrestored', () => void this.restoreContext());
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.stop();
      else this.start();
    });
  }

  async initialize(): Promise<void> {
    if (this.options.webgl) {
      try {
        this.lake = new LakeScene(this.canvas);
        await this.lake.initialize();
        this.metrics.renderer = this.lake.renderer;
      } catch (error) {
        console.warn('退回静态场景：', error);
        this.lake = null;
      }
    }
    if (!this.lake) this.showFallback();
    this.resize();
    this.initialized = true;
    this.renderOnce();
    document.body.dataset.sceneReady = 'true';
    this.start();
  }

  private bindPointer(target: HTMLCanvasElement): void {
    target.addEventListener('pointermove', (event) => this.onPointerMove(event), { passive: true });
    target.addEventListener('pointerleave', () => this.setHoverFromScene(null));
    target.addEventListener('click', (event) => this.onClick(event));
  }

  private showFallback(): void {
    this.canvas.dataset.hidden = 'true';
    this.fallback.src = `./assets/lake/fallback-${PERIOD_IDS[Math.round(this.targetDay) % PERIOD_IDS.length]}.jpg`;
    this.fallback.hidden = false;
    this.fallbackCanvas = this.constellation.canvas;
    this.fallbackCanvas.className = 'constellation-fallback';
    if (!this.fallbackBound) {
      // 回退时星座画布盖在最上面，点击要落在它身上
      this.bindPointer(this.fallbackCanvas);
      this.fallbackBound = true;
    }
    this.fallback.after(this.fallbackCanvas);
    this.metrics.renderer = '静态图';
  }

  private async restoreContext(): Promise<void> {
    if (!this.lake) return;
    try {
      await this.lake.initialize();
    } catch (error) {
      console.warn('场景没有恢复，继续用静态图：', error);
      return;
    }
    this.fallbackCanvas?.remove();
    this.fallbackCanvas = null;
    this.fallback.hidden = true;
    this.canvas.dataset.hidden = 'false';
    this.metrics.renderer = this.lake.renderer;
    this.resize();
    this.dispatchEvent(new Event('contextrestored'));
    this.start();
  }

  /** 传入全部记录，内部挑出今天的并按时间正序点亮。 */
  setRecords(records: QuanShuoRecord[], animateNew: boolean): void {
    const today = dayKey(new Date().toISOString());
    const todayRecords = records.filter((record) => dayKey(record.timestamp) === today).sort((a, b) => a.timestamp.localeCompare(b.timestamp));
    const now = this.clock.now();
    // 静态模式没有后续帧推进动画，新星直接以完成状态出现
    const animate = animateNew && this.initialized && !this.isStatic;
    this.constellation.setRecords(todayRecords, now, animate);
    this.overlayDirty = true;
    if (animate) {
      this.ripple(config.peakX - 0.1, 0.22, 1);
      this.wake();
    }
    this.renderOnce();
  }

  setDay(day: number, immediate = false): void {
    const count = config.periods.length;
    this.targetDay = ((day % count) + count) % count;
    if (immediate || this.clock.isFixed || this.quiet) this.currentDay = this.targetDay;
    this.applyPeriod(this.targetDay);
    this.wake();
    this.renderOnce();
  }

  getDay(): number {
    return this.targetDay;
  }

  setMicrophoneLevel(level: number): void {
    this.microphone = Math.max(0, Math.min(1, level));
    this.wake();
  }

  setForcedVoice(level: number): void {
    this.forcedVoice = Math.max(0, Math.min(1, level));
    this.renderOnce();
  }

  setQuiet(quiet: boolean): void {
    this.quiet = quiet;
    document.documentElement.dataset.quiet = String(quiet);
    if (quiet) {
      this.stop();
      this.constellation.finishAnimations();
      this.overlayDirty = true;
    } else {
      this.start();
    }
    this.renderOnce();
  }

  setHovered(id: string | null): void {
    this.hovered = id;
    this.constellation.setHovered(id);
    this.overlayDirty = true;
    this.renderOnce();
    this.wake();
  }

  setSelected(ids: Set<string>): void {
    this.constellation.setSelected(ids);
    this.overlayDirty = true;
    this.renderOnce();
  }

  /** 水面坐标：x 0–1，y 0（底）到 horizon。 */
  ripple(x: number, y: number, strength: number): void {
    if (this.quiet) return;
    this.lake?.addRipple(x, y, this.clock.now(), strength);
    this.wake();
  }

  /** 复制给 AI：夜空有流星，白天流星看不见，水面的涟漪负责反馈。 */
  celebrate(): void {
    if (this.quiet) return;
    this.lake?.launchMeteor(this.clock.now(), Math.random());
    this.ripple(0.6, 0.24, 0.9);
  }

  /** 让截图的倒影出现在湖面。rect 为 CSS 像素。 */
  setStageImage(image: TexImageSource | null, rect: DOMRect | null): void {
    if (!this.lake) return;
    if (!image || !rect) {
      if (this.stage && this.isStatic) {
        // 静态模式没有后续帧淡出，必须当场收掉
        this.stage = null;
        this.lake.setStageAlpha(0);
      } else if (this.stage) {
        this.stage.target = 0;
      }
      this.wake();
      this.renderOnce();
      return;
    }
    const normalized = this.normalize(rect);
    this.stage = { rect: normalized, alpha: this.isStatic ? 1 : 0, target: 1 };
    this.lake.setStage(image, normalized, this.stage.alpha);
    this.ripple(((rect.left + rect.right) / 2) / innerWidth, config.horizon - 0.012, 0.8);
    this.wake();
    this.renderOnce();
  }

  updateStageRect(rect: DOMRect): void {
    if (!this.stage || !this.lake) return;
    this.stage.rect = this.normalize(rect);
    this.lake.setStage(null, this.stage.rect, this.stage.alpha);
    this.renderOnce();
  }

  setTime(time: number): void {
    this.clock.setTime(time);
    this.clock.setFixed(true);
    this.stop();
    this.renderOnce();
  }

  renderOnce(): void {
    if (!this.initialized || document.hidden) return;
    this.draw(0);
  }

  getMetrics(): SceneMetrics {
    return { ...this.metrics, voice: this.voice, day: this.currentDay, scale: this.resolutionScale };
  }

  private get isStatic(): boolean {
    return this.quiet || this.clock.isFixed;
  }

  private normalize(rect: DOMRect): [number, number, number, number] {
    return [rect.left / innerWidth, 1 - rect.bottom / innerHeight, rect.right / innerWidth, 1 - rect.top / innerHeight];
  }

  private applyPeriod(day: number): void {
    const id = PERIOD_IDS[Math.round(day) % PERIOD_IDS.length];
    document.documentElement.dataset.period = id;
    const dark = id === 'dusk' || id === 'night';
    this.constellation.setTheme(dark);
    this.overlayDirty = true;
    this.dispatchEvent(new CustomEvent('period', { detail: { id, day } }));
  }

  private wake(): void {
    this.lastInteraction = performance.now();
    this.start();
  }

  private start(): void {
    if (!this.initialized || this.quiet || this.clock.isFixed || document.hidden || this.frameHandle) return;
    this.lastFrame = performance.now();
    this.frameHandle = requestAnimationFrame((time) => this.tick(time));
  }

  private stop(): void {
    if (this.frameHandle) cancelAnimationFrame(this.frameHandle);
    this.frameHandle = 0;
  }

  private tick(now: number): void {
    this.frameHandle = 0;
    if (document.hidden || this.quiet || this.clock.isFixed) return;
    const elapsed = now - this.lastFrame;
    if (elapsed >= 1000 / this.targetFps() - 1) {
      const delta = Math.min(0.1, elapsed / 1000);
      this.lastFrame = now;
      this.draw(delta);
      this.metrics.fps = Math.round(1000 / Math.max(1, elapsed));
      this.metrics.frameMs = Math.round(elapsed * 10) / 10;
      this.adaptResolution(elapsed);
    }
    this.frameHandle = requestAnimationFrame((time) => this.tick(time));
  }

  /** 平时星空几乎不动，30fps 足够；有交互或涟漪时才跑满，省电。 */
  private targetFps(): number {
    if (!document.hasFocus()) return config.frame.blurredFps;
    const busy = performance.now() - this.lastInteraction < 1600 || this.voice > 0.01 || Math.abs(this.targetDay - this.currentDay) > 0.002 || (this.stage && this.stage.alpha !== this.stage.target) || this.lake?.hasActiveRipples(this.clock.now());
    return busy ? config.frame.activeFps : config.frame.idleFps;
  }

  private adaptResolution(elapsed: number): void {
    if (!this.lake) return;
    const budget = 1000 / this.targetFps();
    this.slowFrames = elapsed > budget * 1.8 ? this.slowFrames + 1 : Math.max(0, this.slowFrames - 1);
    if (this.slowFrames > 45 && this.resolutionScale > 0.5) {
      this.resolutionScale = Math.max(0.5, this.resolutionScale - 0.2);
      this.slowFrames = 0;
      this.lake.setResolutionScale(this.resolutionScale);
    }
  }

  private draw(delta: number): void {
    const time = this.clock.now();
    if (!this.clock.isFixed) {
      // 时段是环，沿最短方向走：3.9 到 0.1 是向前跨过零点，不是倒退一圈
      const count = config.periods.length;
      const difference = ((this.targetDay - this.currentDay + count / 2) % count + count) % count - count / 2;
      this.currentDay = (this.currentDay + difference * (1 - Math.exp(-delta * 3.4)) + count) % count;
    }
    const target = this.quiet ? 0 : Math.max(this.microphone, this.forcedVoice ?? 0);
    if (this.clock.isFixed) this.voice = target;
    else {
      const halfLife = (target > this.voice ? config.voice.attackHalfLifeMs : config.voice.releaseHalfLifeMs) / 1000;
      this.voice += (target - this.voice) * (1 - Math.pow(2, -delta / halfLife));
    }
    this.spawnVoiceRipple(time);
    this.parallax += (this.parallaxTarget - this.parallax) * (1 - Math.exp(-delta * 2.6));
    if (this.stage) {
      if (this.stage.alpha !== this.stage.target) {
        const step = delta * 2.4;
        this.stage.alpha = this.stage.alpha < this.stage.target ? Math.min(this.stage.target, this.stage.alpha + step) : Math.max(this.stage.target, this.stage.alpha - step);
        if (this.quiet) this.stage.alpha = this.stage.target;
      }
      this.lake?.setStageAlpha(this.stage.alpha);
      if (this.stage.alpha === 0 && this.stage.target === 0) this.stage = null;
    }
    if (this.overlayDirty || this.constellation.isAnimating(time)) {
      this.constellation.draw(time);
      if (!this.fallbackCanvas) this.lake?.updateOverlay(this.constellation.canvas);
      this.overlayDirty = false;
    }
    this.lake?.render({ time, day: this.currentDay, voice: this.voice, parallax: this.parallax });
    this.metrics.voice = this.voice;
    this.metrics.day = this.currentDay;
    this.dispatchEvent(new CustomEvent('metrics', { detail: this.getMetrics() }));
  }

  private spawnVoiceRipple(time: number): void {
    if (this.voice < config.ripple.voiceThreshold || this.quiet) return;
    const now = this.clock.isFixed ? time * 1000 : performance.now();
    if (now - this.lastVoiceRipple < config.ripple.voiceEveryMs) return;
    this.lastVoiceRipple = now;
    const focus = this.stage && this.stage.target > 0 ? (this.stage.rect[0] + this.stage.rect[2]) / 2 : config.peakX - 0.06;
    this.lake?.addRipple(focus + (Math.random() - 0.5) * 0.04, 0.16 + Math.random() * 0.08, time, 0.5 + this.voice * 0.8);
  }

  private onPointerMove(event: PointerEvent): void {
    this.parallaxTarget = event.clientX / innerWidth * 2 - 1;
    const star = this.constellation.starAt(event.clientX, event.clientY);
    this.setHoverFromScene(star?.id ?? null);
    this.canvas.style.cursor = star ? 'pointer' : '';
    const onWater = 1 - event.clientY / innerHeight < config.horizon - 0.01;
    const now = performance.now();
    if (onWater && now - this.lastPointerRipple > config.ripple.pointerEveryMs) {
      this.lastPointerRipple = now;
      this.ripple(event.clientX / innerWidth, 1 - event.clientY / innerHeight, 0.22);
    }
    this.wake();
  }

  private onClick(event: MouseEvent): void {
    const star = this.constellation.starAt(event.clientX, event.clientY);
    if (star) {
      this.dispatchEvent(new CustomEvent('starselect', { detail: star.id }));
      return;
    }
    const y = 1 - event.clientY / innerHeight;
    if (y < config.horizon - 0.005) this.ripple(event.clientX / innerWidth, y, 1);
  }

  private setHoverFromScene(id: string | null): void {
    if (id === this.hovered) return;
    this.setHovered(id);
    const star = id ? this.constellation.star(id) : undefined;
    this.dispatchEvent(new CustomEvent('starhover', { detail: id && star ? { id, x: star.x, y: star.y } : null }));
  }

  private resize(): void {
    this.lake?.resize();
    const left = 12 + 368 + 40;
    this.constellation.resize(innerWidth, innerHeight, {
      left,
      right: innerWidth - 56,
      top: 56,
      bottom: innerHeight * (1 - config.horizon - 0.25),
    });
    this.overlayDirty = true;
    this.renderOnce();
    this.dispatchEvent(new Event('resized'));
  }
}
