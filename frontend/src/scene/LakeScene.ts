import config from '../config/scene.config.json';
import vertexSource from './shaders/lake.vert.glsl?raw';
import fragmentSource from './shaders/lake.frag.glsl?raw';

type PeriodConfig = (typeof config.periods)[number];
type Rgb = [number, number, number];

export interface LakeFrame {
  time: number;
  day: number;
  voice: number;
  parallax: number;
}

interface Ripple { x: number; y: number; start: number; strength: number }

const COLOR_UNIFORMS: Array<[keyof PeriodConfig, string]> = [
  ['skyTop', 'uSkyTop'], ['skyMid', 'uSkyMid'], ['skyLow', 'uSkyLow'], ['skyHorizon', 'uSkyHorizon'],
  ['ambient', 'uAmbient'], ['key', 'uKey'], ['haze', 'uHaze'], ['snow', 'uSnow'], ['rock', 'uRock'],
  ['waterDeep', 'uWaterDeep'], ['waterShallow', 'uWaterShallow'], ['glow', 'uGlowColor'],
];

export function hexToRgb(hex: string): Rgb {
  const value = Number.parseInt(hex.slice(1), 16);
  return [(value >> 16 & 255) / 255, (value >> 8 & 255) / 255, (value & 255) / 255];
}

/** 把时段配置按连续的 day 插值。时段是环，3 之后回到 0。 */
export function periodBlend(day: number): { from: PeriodConfig; to: PeriodConfig; mix: number } {
  const count = config.periods.length;
  const wrapped = ((day % count) + count) % count;
  const index = Math.floor(wrapped);
  return { from: config.periods[index], to: config.periods[(index + 1) % count], mix: wrapped - index };
}

/** WebGL2 全屏湖景。只负责绘制，不持有业务数据。 */
export class LakeScene {
  private gl: WebGL2RenderingContext | null = null;
  private uniforms = new Map<string, WebGLUniformLocation | null>();
  private ripples: Ripple[] = [];
  private overlayTexture: WebGLTexture | null = null;
  private stageTexture: WebGLTexture | null = null;
  private rippleBuffer = new Float32Array(config.ripple.maximum * 4);
  private meteor = { start: -100, duration: 1.3, strength: 0, seed: 0 };
  private stageRect: [number, number, number, number] = [0, 0, 1, 1];
  private stageAlpha = 0;
  private resolutionScale = 1;
  renderer = '未启动';

  constructor(private canvas: HTMLCanvasElement) {}

  async initialize(): Promise<void> {
    const gl = this.canvas.getContext('webgl2', {
      alpha: false,
      antialias: false,
      depth: false,
      stencil: false,
      preserveDrawingBuffer: true,
      powerPreference: 'high-performance',
    });
    if (!gl) throw new Error('WebGL2 不可用');
    this.gl = gl;
    const program = gl.createProgram();
    if (!program) throw new Error('无法创建 WebGL 程序');
    gl.attachShader(program, this.compile(gl.VERTEX_SHADER, vertexSource));
    gl.attachShader(program, this.compile(gl.FRAGMENT_SHADER, fragmentSource));
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? '着色器链接失败');
    gl.useProgram(program);
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const position = gl.getAttribLocation(program, 'aPosition');
    gl.enableVertexAttribArray(position);
    gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);

    const count = gl.getProgramParameter(program, gl.ACTIVE_UNIFORMS) as number;
    for (let index = 0; index < count; index++) {
      const info = gl.getActiveUniform(program, index);
      if (!info) continue;
      const name = info.name.replace('[0]', '');
      this.uniforms.set(name, gl.getUniformLocation(program, info.name));
    }

    const [terrainA, terrainB] = await Promise.all(config.terrain.map(loadImage));
    this.createTexture(0, terrainA, 'uTerrainA', true);
    this.createTexture(1, terrainB, 'uTerrainB', true);
    this.overlayTexture = this.createTexture(2, emptyPixel(), 'uOverlay', false);
    this.stageTexture = this.createTexture(3, emptyPixel(), 'uStage', false);
    gl.uniform1f(this.uniform('uHorizon'), config.horizon);

    const debug = gl.getExtension('WEBGL_debug_renderer_info');
    this.renderer = debug ? String(gl.getParameter(debug.UNMASKED_RENDERER_WEBGL)) : 'WebGL2';
    this.resize();
  }

  resize(): void {
    const gl = this.gl;
    if (!gl) return;
    const ratio = Math.min(devicePixelRatio || 1, config.maxDevicePixelRatio) * this.resolutionScale;
    const width = Math.max(1, Math.round(this.canvas.clientWidth * ratio));
    const height = Math.max(1, Math.round(this.canvas.clientHeight * ratio));
    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
    }
    gl.viewport(0, 0, width, height);
    gl.uniform2f(this.uniform('uResolution'), width, height);
    gl.uniform1f(this.uniform('uPixel'), ratio);
    const aspect = width / height;
    const display = Math.max(config.terrainHeight * config.terrainAspect, aspect * 1.42);
    gl.uniform3f(this.uniform('uTerrainPlace'), aspect * config.peakX, display, display / config.terrainAspect);
  }

  /** 帧耗时过高时调用，降低内部分辨率。 */
  setResolutionScale(scale: number): void {
    this.resolutionScale = scale;
    this.resize();
  }

  addRipple(x: number, y: number, start: number, strength: number): void {
    this.ripples.push({ x, y, start, strength });
    if (this.ripples.length > config.ripple.maximum) this.ripples.shift();
  }

  hasActiveRipples(time: number): boolean {
    return this.ripples.some((ripple) => time - ripple.start < config.ripple.lifetime);
  }

  launchMeteor(time: number, seed: number): void {
    this.meteor = { start: time, duration: 1.3, strength: 1, seed };
  }

  updateOverlay(source: TexImageSource): void {
    const gl = this.gl;
    if (!gl || !this.overlayTexture) return;
    gl.activeTexture(gl.TEXTURE2);
    gl.bindTexture(gl.TEXTURE_2D, this.overlayTexture);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
  }

  /** rect 为归一化坐标，原点在左下角。 */
  setStage(image: TexImageSource | null, rect: [number, number, number, number], alpha: number): void {
    const gl = this.gl;
    if (!gl || !this.stageTexture) return;
    if (image) {
      gl.activeTexture(gl.TEXTURE3);
      gl.bindTexture(gl.TEXTURE_2D, this.stageTexture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
    }
    this.stageRect = rect;
    this.stageAlpha = alpha;
  }

  setStageAlpha(alpha: number): void {
    this.stageAlpha = alpha;
  }

  render(frame: LakeFrame): void {
    const gl = this.gl;
    if (!gl) return;
    const { from, to, mix } = periodBlend(frame.day);
    const lerp = (a: number, b: number) => a + (b - a) * mix;
    COLOR_UNIFORMS.forEach(([key, name]) => {
      const a = hexToRgb(from[key] as string), b = hexToRgb(to[key] as string);
      gl.uniform3f(this.uniform(name), lerp(a[0], b[0]), lerp(a[1], b[1]), lerp(a[2], b[2]));
    });
    gl.uniform4f(this.uniform('uSun'), lerp(from.sun[0], to.sun[0]), lerp(from.sun[1], to.sun[1]), lerp(from.sun[2], to.sun[2]), lerp(from.sun[3], to.sun[3]));
    gl.uniform1f(this.uniform('uStars'), lerp(from.stars, to.stars));
    gl.uniform1f(this.uniform('uClouds'), lerp(from.clouds, to.clouds));
    gl.uniform1f(this.uniform('uSparkle'), lerp(from.sparkle, to.sparkle));
    gl.uniform1f(this.uniform('uGlint'), lerp(from.glint, to.glint));
    gl.uniform1f(this.uniform('uTime'), frame.time);
    gl.uniform1f(this.uniform('uVoice'), frame.voice);
    gl.uniform1f(this.uniform('uParallax'), frame.parallax * config.parallaxStrength);
    gl.uniform4f(this.uniform('uMeteor'), this.meteor.start, this.meteor.duration, this.meteor.strength, this.meteor.seed);
    gl.uniform4f(this.uniform('uStageRect'), ...this.stageRect);
    gl.uniform1f(this.uniform('uStageAlpha'), this.stageAlpha);

    this.ripples = this.ripples.filter((ripple) => frame.time - ripple.start < config.ripple.lifetime && frame.time >= ripple.start - 0.01);
    this.ripples.forEach((ripple, index) => this.rippleBuffer.set([ripple.x, ripple.y, ripple.start, ripple.strength], index * 4));
    gl.uniform4fv(this.uniform('uRipples'), this.rippleBuffer);
    gl.uniform1i(this.uniform('uRippleCount'), this.ripples.length);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  private uniform(name: string): WebGLUniformLocation | null {
    return this.uniforms.get(name) ?? null;
  }

  private compile(type: number, source: string): WebGLShader {
    const gl = this.gl!;
    const shader = gl.createShader(type);
    if (!shader) throw new Error('无法创建着色器');
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) ?? '着色器编译失败');
    return shader;
  }

  private createTexture(unit: number, source: TexImageSource, uniform: string, mip: boolean): WebGLTexture {
    const gl = this.gl!;
    const texture = gl.createTexture();
    if (!texture) throw new Error('无法创建纹理');
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, mip ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    if (mip) gl.generateMipmap(gl.TEXTURE_2D);
    gl.uniform1i(this.uniform(uniform), unit);
    return texture;
  }
}

function emptyPixel(): ImageData {
  return new ImageData(1, 1);
}

function loadImage(source: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error(`场景素材加载失败：${source}`));
    image.src = source;
  });
}
