import type { QuanShuoRecord } from '../bridge/types';
import constellation from '../config/constellation.json';
import { seededRandom } from './random';

export interface StarPoint { id: string; x: number; y: number; size: number; bornAt: number }

interface Region { left: number; right: number; top: number; bottom: number }

/** 把今天的记录画成鲸的星座。画布会作为纹理交给湖面，所以星也有倒影。 */
export class ConstellationLayer {
  readonly canvas = document.createElement('canvas');
  private context = this.canvas.getContext('2d')!;
  private stars: StarPoint[] = [];
  private records: QuanShuoRecord[] = [];
  private hovered: string | null = null;
  private selected = new Set<string>();
  private width = 1;
  private height = 1;
  private region: Region = { left: 0, right: 1, top: 0, bottom: 1 };
  private dark = true;

  resize(width: number, height: number, region: Region): void {
    this.width = Math.max(1, Math.round(width));
    this.height = Math.max(1, Math.round(height));
    this.canvas.width = this.width;
    this.canvas.height = this.height;
    this.region = region;
    this.layout();
  }

  setTheme(dark: boolean): void {
    this.dark = dark;
  }

  setHovered(id: string | null): void {
    this.hovered = id;
  }

  setSelected(ids: Set<string>): void {
    this.selected = ids;
  }

  /** records 须按时间正序：第一条点亮鲸尾。 */
  setRecords(records: QuanShuoRecord[], now: number, animateNewest: boolean): void {
    const known = new Set(this.records.map((record) => record.id));
    this.records = records;
    this.layout();
    if (animateNewest) this.stars.forEach((star) => { if (!known.has(star.id)) star.bornAt = now; });
  }

  starAt(x: number, y: number): StarPoint | null {
    let best: StarPoint | null = null;
    let bestDistance = 20;
    this.stars.forEach((star) => {
      const distance = Math.hypot(star.x - x, star.y - y);
      if (distance < bestDistance) { best = star; bestDistance = distance; }
    });
    return best;
  }

  star(id: string): StarPoint | undefined {
    return this.stars.find((star) => star.id === id);
  }

  /** 进入静态模式时结束入场动画，否则星星会停在尺寸接近零的第一帧。 */
  finishAnimations(): void {
    this.stars.forEach((star) => { star.bornAt = -100; });
  }

  isAnimating(now: number): boolean {
    return this.stars.some((star) => now - star.bornAt < 1.1);
  }

  draw(now: number): void {
    const context = this.context;
    context.clearRect(0, 0, this.width, this.height);
    const points = this.chartPoints();
    const lit = Math.min(this.stars.length, points.length);
    this.drawLines(points, lit);
    points.forEach((point, index) => {
      if (index >= lit) this.drawDim(point.x, point.y);
    });
    this.stars.forEach((star) => this.drawStar(star, now));
  }

  private drawLines(points: Array<{ x: number; y: number }>, lit: number): void {
    const context = this.context;
    context.lineCap = 'round';
    constellation.lines.forEach(([from, to]) => {
      const a = points[from], b = points[to];
      if (!a || !b) return;
      const on = from < lit && to < lit;
      context.beginPath();
      context.moveTo(a.x, a.y);
      context.lineTo(b.x, b.y);
      context.lineWidth = 1;
      context.setLineDash(on ? [] : [1, 5]);
      context.strokeStyle = on
        ? (this.dark ? 'rgba(190, 228, 255, .46)' : 'rgba(255, 255, 255, .95)')
        : (this.dark ? 'rgba(190, 215, 255, .13)' : 'rgba(255, 255, 255, .55)');
      context.stroke();
    });
    context.setLineDash([]);
  }

  private drawDim(x: number, y: number): void {
    const context = this.context;
    context.fillStyle = this.dark ? 'rgba(200, 220, 255, .30)' : 'rgba(255, 255, 255, .8)';
    context.beginPath();
    context.arc(x, y, 1.1, 0, Math.PI * 2);
    context.fill();
  }

  private drawStar(star: StarPoint, now: number): void {
    const context = this.context;
    const age = now - star.bornAt;
    const flare = age < 1.1 ? Math.exp(-age * 3.2) * 2.4 : 0;
    const appear = age < 0 ? 0 : Math.min(1, age * 4 + 0.001);
    const active = star.id === this.hovered || this.selected.has(star.id);
    const size = star.size * (active ? 1.5 : 1) * (1 + flare * 0.7) * appear;
    if (size <= 0) return;
    const glowRadius = size * (active ? 7 : 4.4);
    const glow = context.createRadialGradient(star.x, star.y, 0, star.x, star.y, glowRadius);
    const tint = this.dark ? '169, 234, 242' : '255, 255, 255';
    glow.addColorStop(0, `rgba(${tint}, ${active ? 0.62 : 0.42})`);
    glow.addColorStop(1, `rgba(${tint}, 0)`);
    context.fillStyle = glow;
    context.beginPath();
    context.arc(star.x, star.y, glowRadius, 0, Math.PI * 2);
    context.fill();
    context.fillStyle = this.dark ? '#ffffff' : '#ffffff';
    fourPoint(context, star.x, star.y, size * 2.3, size * 0.34);
    if (!this.dark) {
      context.strokeStyle = 'rgba(35, 68, 168, .35)';
      context.lineWidth = 0.75;
      context.stroke();
    }
  }

  private chartPoints(): Array<{ x: number; y: number; weight: number }> {
    const [viewWidth, viewHeight] = constellation.viewBox;
    const region = this.region;
    const availableWidth = region.right - region.left;
    const availableHeight = region.bottom - region.top;
    const scale = Math.min(availableWidth / viewWidth, availableHeight / viewHeight);
    const originX = region.left + (availableWidth - viewWidth * scale) / 2;
    const originY = region.top + (availableHeight - viewHeight * scale) / 2;
    return constellation.stars.map((star) => ({ x: originX + star.x * scale, y: originY + star.y * scale, weight: star.weight }));
  }

  private layout(): void {
    const points = this.chartPoints();
    const previous = new Map(this.stars.map((star) => [star.id, star.bornAt]));
    const random = seededRandom(29);
    this.stars = this.records.map((record, index) => {
      const point = points[index];
      if (point) return { id: record.id, x: point.x, y: point.y, size: 2.3 * point.weight, bornAt: previous.get(record.id) ?? -100 };
      // 鲸画满之后，多出来的记录散在它周围
      const region = this.region;
      return {
        id: record.id,
        x: region.left + random() * (region.right - region.left),
        y: region.top + random() * (region.bottom - region.top),
        size: 1.5 + random() * 0.6,
        bornAt: previous.get(record.id) ?? -100,
      };
    });
  }
}

function fourPoint(context: CanvasRenderingContext2D, x: number, y: number, outer: number, inner: number): void {
  context.beginPath();
  for (let index = 0; index < 8; index++) {
    const radius = index % 2 === 0 ? outer : inner;
    const angle = index * Math.PI / 4 - Math.PI / 2;
    const px = x + Math.cos(angle) * radius, py = y + Math.sin(angle) * radius;
    if (index === 0) context.moveTo(px, py); else context.lineTo(px, py);
  }
  context.closePath();
  context.fill();
}
