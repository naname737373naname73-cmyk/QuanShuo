import type { SceneMetrics, SceneRenderer } from '../scene/SceneRenderer';
import { element } from '../utils/html';

/** Ctrl+Shift+D：调时段、声音和时间，看帧率。调好的值写回 scene.config.json。 */
export class DebugPanel {
  private metrics: HTMLElement;

  constructor(private root: HTMLElement, scene: SceneRenderer, open: boolean) {
    root.dataset.hidden = String(!open);
    this.metrics = element('div', 'debug-metrics');
    root.append(this.metrics);
    this.slider('时段', 0, 3, 0.01, scene.getDay(), (value) => scene.setDay(value, true));
    this.slider('声音', 0, 1, 0.01, 0, (value) => scene.setForcedVoice(value));
    this.slider('固定时间', 0, 120, 0.1, 0, (value) => scene.setTime(value));
    const ripple = element('button', 'secondary-button', '投一颗石子') as HTMLButtonElement;
    ripple.addEventListener('click', () => scene.ripple(0.62, 0.2, 1));
    const meteor = element('button', 'secondary-button', '放一颗流星') as HTMLButtonElement;
    meteor.addEventListener('click', () => scene.celebrate());
    root.append(ripple, meteor);
    scene.addEventListener('metrics', (event) => {
      if (root.dataset.hidden === 'true') return;
      const value = (event as CustomEvent<SceneMetrics>).detail;
      this.metrics.textContent = `${value.fps}fps ${value.frameMs}ms 缩放${value.scale.toFixed(1)} 声音${value.voice.toFixed(2)} ${value.renderer}`;
    });
  }

  toggle(): void {
    this.root.dataset.hidden = String(this.root.dataset.hidden !== 'true');
  }

  private slider(label: string, min: number, max: number, step: number, value: number, change: (value: number) => void): void {
    const row = element('label');
    const input = document.createElement('input');
    input.type = 'range';
    input.min = String(min);
    input.max = String(max);
    input.step = String(step);
    input.value = String(value);
    const output = document.createElement('output');
    output.textContent = input.value;
    input.addEventListener('input', () => {
      output.textContent = input.value;
      change(Number(input.value));
    });
    row.append(element('span', undefined, label), input, output);
    this.root.append(row);
  }
}
