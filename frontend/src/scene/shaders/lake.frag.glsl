#version 300 es
// 星湖主场景：一个片元完成天空、星、远山打光和湖面。
// 输入：烘焙好的地形光照数据 uTerrainA/B，时段配色 u*，涟漪数组 uRipples。
// 输出：sRGB 颜色。文档见 docs/frontend/SCENE.md。
precision highp float;

in vec2 vUv;
out vec4 fragColor;

#define MAX_RIPPLES 12

uniform vec2 uResolution;      // 画布像素尺寸
uniform float uPixel;          // 1 个 CSS 像素对应多少画布像素
uniform float uTime;           // 秒，可被 ?t= 固定
uniform float uHorizon;        // 地平线高度，0=底，1=顶
uniform vec3 uTerrainPlace;    // x=主峰屏幕 x（aspect 单位），y=贴图显示宽度，z=贴图显示高度
uniform float uParallax;       // 鼠标视差

// 时段配色：由 JS 按 uDay 连续插值后传入
uniform vec3 uSkyTop, uSkyMid, uSkyLow, uSkyHorizon;
uniform vec3 uAmbient, uKey, uHaze, uSnow, uRock;
uniform vec3 uWaterDeep, uWaterShallow, uGlowColor;
uniform vec4 uSun;             // xy=光源屏幕位置（归一化），z=光晕强度，w=光晕半径
uniform float uStars, uClouds, uSparkle, uGlint;

uniform sampler2D uTerrainA;   // R=法线x G=法线y B=受光
uniform sampler2D uTerrainB;   // R=积雪 G=AO B=深度
uniform sampler2D uOverlay;    // 记录星座层（预乘 alpha），会被湖面倒映
uniform sampler2D uStage;      // 当前选中截图，只用于倒影
uniform vec4 uStageRect;       // x0,y0,x1,y1（归一化，左下为原点）
uniform float uStageAlpha;

uniform vec4 uRipples[MAX_RIPPLES]; // x,y=水面归一化坐标 z=起始时间 w=强度
uniform int uRippleCount;
uniform float uVoice;          // 0–1，麦克风电平，给整片水面增加细浪
uniform vec4 uMeteor;          // x=开始时间 y=持续 z=强度 w=随机种子

const vec3 LIGHT_DIR = vec3(-0.74, 0.42, -0.44);

float hash21(vec2 p) {
  vec3 q = fract(vec3(p.xyx) * 0.1031);
  q += dot(q, q.yzx + 33.33);
  return fract((q.x + q.y) * q.z);
}
vec2 hash22(vec2 p) {
  vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
  q += dot(q, q.yzx + 33.33);
  return fract((q.xx + q.yz) * q.zy);
}
float noise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash21(i), hash21(i + vec2(1, 0)), u.x), mix(hash21(i + vec2(0, 1)), hash21(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p, int octaves) {
  float sum = 0.0, amp = 0.5;
  const mat2 rot = mat2(0.8, -0.6, 0.6, 0.8);
  for (int i = 0; i < octaves; i++) { sum += amp * noise(p); p = rot * p * 2.03; amp *= 0.5; }
  return sum;
}

// 四芒星：两条互相垂直的细长光脉冲，取自参考图水面的闪光
float glint(vec2 d, float size) {
  vec2 a = abs(d) / size;
  float cross = exp(-a.x * a.y * 16.0) * exp(-(a.x + a.y) * 2.1);
  float core = exp(-dot(d, d) / (size * size * 0.018));
  return cross * 0.55 + core;
}

vec3 starLayer(vec2 px, float cell, float density, float size, float seed) {
  vec2 grid = px / cell;
  vec2 id = floor(grid);
  float pick = hash21(id + seed);
  if (pick > density) return vec3(0.0);
  vec2 jitter = hash22(id + seed + 3.7) * 0.8 + 0.1;
  vec2 d = (fract(grid) - jitter) * cell;
  float mag = hash21(id + seed + 9.1);
  mag = mag * mag * mag * mag;
  float twinkle = 0.78 + 0.22 * sin(uTime * (0.7 + mag * 2.6) + pick * 94.0);
  float r = size * (0.52 + mag * 0.9);
  float light = exp(-dot(d, d) / (r * r)) * (0.16 + mag * 1.5) * twinkle;
  float t = hash21(id + seed + 21.3);
  vec3 tint = mix(vec3(0.74, 0.84, 1.0), vec3(1.0, 0.90, 0.78), t * t);
  return tint * light;
}

// 天空，p 为 aspect 单位坐标，y 从 0 到 1
vec3 skyColor(vec2 p, float aspect, bool reflected) {
  float above = clamp((p.y - uHorizon) / (1.0 - uHorizon), 0.0, 1.0);
  vec3 c = mix(uSkyHorizon, uSkyLow, smoothstep(0.0, 0.26, above));
  c = mix(c, uSkyMid, smoothstep(0.14, 0.60, above));
  c = mix(c, uSkyTop, smoothstep(0.46, 1.0, above));

  // 光晕：地平线上的暖光或正午左上角的白光
  vec2 toSun = vec2(p.x - uSun.x * aspect, (p.y - uSun.y) * 1.7);
  float glow = exp(-length(toSun) / max(0.05, uSun.w));
  c += uGlowColor * glow * uSun.z;
  c += uGlowColor * exp(-max(0.0, p.y - uHorizon) * 10.0) * uSun.z * 0.34;

  // 高云：只在白天和黄昏出现
  if (uClouds > 0.001) {
    vec2 q = vec2(p.x * 1.6 + uTime * 0.002, p.y * 5.0);
    float cloud = smoothstep(0.46, 0.84, fbm(q + fbm(q * 0.7, 3) * 1.6, 5));
    c = mix(c, mix(c, vec3(1.0, 0.98, 0.96), 0.72), cloud * uClouds * smoothstep(0.02, 0.32, above));
  }

  if (uStars > 0.001) {
    vec2 px = p * uResolution.y / uPixel + vec2(uParallax * 6.0, 0.0);
    // 银河：沿对角线的淡带，同时提高这里的星密度
    float band = exp(-pow((p.y - 0.78 + (p.x / aspect - 0.5) * 0.62) * 3.4, 2.0));
    float dust = fbm(p * 5.0 + 4.0, 5);
    float milky = band * smoothstep(0.32, 0.72, dust);
    c += vec3(0.33, 0.37, 0.56) * milky * 0.34 * uStars;
    vec3 stars = starLayer(px, 34.0, 0.34, 1.15, 41.0);
    if (!reflected) {
      stars += starLayer(px, 4.2, 0.18 + milky * 0.5, 0.66, 1.0) * 0.8;
      stars += starLayer(px, 9.5, 0.22 + milky * 0.2, 0.78, 17.0) * 0.8;
    }
    // 带四芒的亮星
    vec2 grid = px / 150.0;
    vec2 id = floor(grid);
    if (hash21(id + 7.0) < 0.24) {
      vec2 jitter = hash22(id + 11.0) * 0.7 + 0.15;
      vec2 d = (fract(grid) - jitter) * 150.0;
      float twinkle = 0.75 + 0.25 * sin(uTime * 1.4 + hash21(id) * 40.0);
      float mag = 0.5 + hash21(id + 2.0);
      stars += vec3(0.9, 0.95, 1.0) * glint(d, 5.4 * mag) * twinkle * 0.9;
    }
    // 一颗孤悬的亮行星，呼应参考图
    vec2 planet = (p - vec2(aspect * 0.46, uHorizon + (1.0 - uHorizon) * 0.54)) * uResolution.y / uPixel;
    stars += vec3(1.0, 0.96, 0.9) * (glint(planet, 8.5) * 1.5 + exp(-length(planet) / 15.0) * 0.20);
    float fade = smoothstep(0.02, 0.42, above);
    c += stars * uStars * fade;
  }

  // 流星：复制给 AI 时划过
  float age = uTime - uMeteor.x;
  if (uMeteor.z > 0.0 && age > 0.0 && age < uMeteor.y) {
    float k = age / uMeteor.y;
    float seed = uMeteor.w;
    vec2 a = vec2(aspect * (0.42 + seed * 0.2), uHorizon + (1.0 - uHorizon) * 0.92);
    vec2 b = a + vec2(aspect * 0.42, -(1.0 - uHorizon) * 0.34);
    vec2 head = mix(a, b, k);
    vec2 tail = mix(a, b, max(0.0, k - 0.32));
    vec2 pa = p - tail, ba = head - tail;
    float h = clamp(dot(pa, ba) / max(0.0001, dot(ba, ba)), 0.0, 1.0);
    float d = length(pa - ba * h) * uResolution.y / uPixel;
    float env = sin(k * 3.14159);
    c += vec3(1.0, 0.97, 0.9) * exp(-d * d / 2.2) * h * h * env * uMeteor.z * 1.5;
  }
  return c;
}

// 远山打光：z 通道返回覆盖度
vec4 mountain(vec2 p) {
  float u = (p.x - uTerrainPlace.x + uParallax * 0.012) / uTerrainPlace.y + 0.5;
  float v = (p.y - uHorizon) / uTerrainPlace.z;
  if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0) return vec4(0.0);
  vec2 uv = vec2(u, 1.0 - v);
  vec4 a = texture(uTerrainA, uv);
  vec4 b = texture(uTerrainB, uv);
  float coverage = smoothstep(0.012, 0.05, b.b);
  if (coverage <= 0.0) return vec4(0.0);
  vec3 n = vec3(a.r * 2.0 - 1.0, a.g, 0.0);
  n.z = -sqrt(max(0.0, 1.0 - dot(n.xy, n.xy)));
  float diffuse = max(dot(n, normalize(LIGHT_DIR)), 0.0) * a.b;
  vec3 albedo = mix(uRock, uSnow, b.r);
  float sky = 0.56 + 0.44 * n.y;
  vec3 color = albedo * (uAmbient * sky * mix(0.45, 1.0, b.g) + uKey * diffuse);
  // 深度雾：越远越融进地平线
  float far = pow(1.0 - b.b, 1.35);
  color = mix(color, uHaze, clamp(far * 0.9, 0.0, 0.7));
  float lift = max(0.0, p.y - uHorizon);
  float mist = exp(-lift * 15.0);
  color = mix(color, mix(uHaze, uSkyHorizon, 0.45), clamp(mist * 0.92, 0.0, 0.95));
  return vec4(color, coverage);
}

// 水面以上的全部内容，倒影也复用这一函数
vec3 aboveWater(vec2 p, float aspect, bool reflected) {
  vec3 c = skyColor(p, aspect, reflected);
  vec4 ridge = mountain(p);
  c = mix(c, ridge.rgb, ridge.a);
  vec2 overlayUv = vec2(p.x / aspect, p.y);
  vec4 ov = texture(uOverlay, vec2(overlayUv.x, 1.0 - overlayUv.y));
  return c * (1.0 - ov.a) + ov.rgb;
}

vec2 toWorld(vec2 p, float aspect) {
  // 透视：越靠近地平线距离越远
  float below = max(0.0015, (uHorizon - p.y) / uHorizon);
  float z = 1.0 / (below + 0.02);
  return vec2((p.x - aspect * 0.5) * z * 2.1, z * 3.4);
}

vec3 water(vec2 p, float aspect) {
  float below = clamp((uHorizon - p.y) / uHorizon, 0.0, 1.0);
  vec2 w = toWorld(p, aspect);
  float z = 1.0 / (below + 0.02);
  float detail = 1.0 / (1.0 + z * 0.052);

  // 细浪：两组反向漂移的噪声取梯度
  float t = uTime;
  float e = 0.06;
  vec2 q1 = w * 1.3 + vec2(t * 0.23, t * 0.15);
  vec2 q2 = w * 2.9 - vec2(t * 0.31, -t * 0.12);
  float h0 = noise(q1) + noise(q2) * 0.48;
  float hx = noise(q1 + vec2(e * 1.3, 0.0)) + noise(q2 + vec2(e * 2.9, 0.0)) * 0.48;
  float hy = noise(q1 + vec2(0.0, e * 1.3)) + noise(q2 + vec2(0.0, e * 2.9)) * 0.48;
  vec2 slope = vec2(hx - h0, hy - h0) / e;
  float chop = 0.0048 + uVoice * 0.030;
  vec2 normal2 = slope * chop * detail * (0.55 + 0.45 * (1.0 - below));

  // 涟漪：世界坐标里的同心圆，投到屏幕自然成椭圆
  float ring = 0.0;
  for (int i = 0; i < uRippleCount; i++) {
    vec4 r = uRipples[i];
    float age = t - r.z;
    if (age < 0.0 || age > 7.0) continue;
    vec2 source = toWorld(vec2(r.x * aspect, r.y), aspect);
    vec2 delta = w - source;
    float dist = length(delta);
    float front = age * 2.5;
    float envelope = exp(-pow((dist - front) / (0.95 + age * 0.42), 2.0)) * exp(-age * 0.62) * smoothstep(0.0, 0.2, age);
    float wave = sin((dist - front) * 8.6);
    float amount = wave * envelope * r.w;
    normal2 += normalize(delta + 0.0001) * amount * 0.050 * detail;
    ring += max(0.0, amount) * detail;
  }

  // 倒影：竖直方向的扰动远大于水平方向，水面才会有拉长的质感
  float stretch = 1.0 + z * 0.10;
  vec2 mirrored = vec2(p.x, uHorizon + (uHorizon - p.y) * 1.0);
  float calm = smoothstep(0.0, 0.16, below);
  mirrored += vec2(normal2.x * 0.9, normal2.y * 1.6 * stretch) * calm;
  mirrored.y = max(mirrored.y, uHorizon + 0.0006);
  vec3 reflected = aboveWater(mirrored, aspect, true);

  // 选中截图的倒影
  if (uStageAlpha > 0.001) {
    vec2 sp = vec2(mirrored.x / aspect, mirrored.y);
    vec2 size = uStageRect.zw - uStageRect.xy;
    vec2 local = (sp - uStageRect.xy) / size;
    if (local.x > 0.0 && local.x < 1.0 && local.y > 0.0 && local.y < 1.0) {
      vec3 image = texture(uStage, vec2(local.x, 1.0 - local.y)).rgb;
      float edge = smoothstep(0.0, 0.02, local.x) * smoothstep(0.0, 0.02, 1.0 - local.x);
      float fall = smoothstep(1.0, 0.0, (mirrored.y - uStageRect.y) / size.y * 0.92);
      reflected = mix(reflected, image, uStageAlpha * edge * fall * 0.86);
    }
  }

  float fresnel = mix(0.30, 0.97, pow(1.0 - below, 2.4));
  vec3 body = mix(uWaterShallow, uWaterDeep, smoothstep(0.0, 0.72, below));
  // 水下的缓慢亮斑，正午时最明显
  body += uWaterShallow * 0.10 * (noise(w * 0.36 + t * 0.05) - 0.4) * (1.0 - fresnel);
  vec3 c = mix(body, reflected, fresnel);

  // 涟漪高光：细而亮
  c += mix(vec3(1.0), uSkyHorizon, 0.35) * pow(ring, 1.8) * 0.32;

  // 水面闪光：四芒星，集中在光柱下方
  if (uSparkle > 0.001) {
    vec2 px = p * uResolution.y / uPixel;
    float cell = 34.0;
    vec2 grid = px / cell;
    vec2 id = floor(grid);
    float pick = hash21(id + 5.0);
    float column = exp(-pow((p.x / aspect - uSun.x) * 3.2, 2.0));
    float density = 0.09 + column * 0.22;
    if (pick < density) {
      vec2 jitter = hash22(id + 2.0) * 0.8 + 0.1;
      vec2 d = (fract(grid) - jitter) * cell;
      float phase = hash21(id + 9.0) * 6.283;
      float twinkle = pow(max(0.0, sin(t * (0.5 + pick * 8.0) + phase)), 12.0);
      float scale = (0.8 + hash21(id + 4.0) * 2.0) * (0.45 + below * 1.2);
      c += vec3(1.0, 0.99, 0.96) * glint(d, 6.5 * scale) * twinkle * uSparkle;
    }
  }

  // 浅色细线：对应参考图里水面上的光纹
  c += uGlint * pow(max(0.0, noise(vec2(w.x * 0.55 + t * 0.03, w.y * 1.7 - t * 0.05)) - 0.55) * 2.2, 3.0) * detail;
  return c;
}

void main() {
  float aspect = uResolution.x / uResolution.y;
  vec2 p = vec2(vUv.x * aspect, vUv.y);
  vec3 c = p.y > uHorizon ? aboveWater(p, aspect, false) : water(p, aspect);
  // 地平线柔化一像素，避免锯齿硬边
  float seam = 1.0 - smoothstep(0.0, 1.6 / uResolution.y, abs(p.y - uHorizon));
  c = mix(c, uHaze, seam * 0.35);

  // 轻微暗角与抖动，后者消除渐变色带
  vec2 vig = vUv - 0.5;
  c *= 1.0 - dot(vig, vig) * 0.16;
  float dither = hash21(gl_FragCoord.xy + fract(uTime) * 61.0) - 0.5;
  fragColor = vec4(c + dither / 180.0, 1.0);
}
