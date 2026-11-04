# 星湖场景

场景完全程序生成，不含任何参考图。分两步：离线烘焙远山的光照数据，运行时按时段打光。

## 为什么这样做

- 四个时段不需要四张图。山只烘焙一次，颜色全部运行时算，所以时段可以连续拖动。
- 山用 GPU 光线步进烘焙，质量远高于实时能承担的程度；运行时只读两张贴图，4K 全屏一帧约 2ms（RTX 5060 Ti）。
- 不把烘焙放进运行时：弱显卡上可能触发显卡超时，也拖慢启动。

## 烘焙

```bash
cd frontend
node tools/bake/bake-terrain.mjs --w 4320 --h 720 --aa 3   # 写入 public/assets/lake/terrain-*.png
node tools/bake/bake-fallback.mjs                          # 重新生成四张回退图
node tools/bake/snap.mjs <输出目录> "名字=/tools/bake/preview.html?day=3&ripple=1@1600x1000"
node tools/bake/measure.mjs                                # 量一帧的 GPU 耗时
```

`terrain-a.png`：R=法线 x，G=法线 y（朝上），B=主光阴影（1=受光）。
`terrain-b.png`：R=积雪，G=环境遮蔽，B=深度（0=天空，大于 0=山体，越大越近）。
贴图不用透明通道，否则 canvas 导出时的预乘会破坏数据。

地形写在 `tools/bake/terrain.html`：

| 位置 | 作用 | 调整效果 |
| --- | --- | --- |
| `envelope` 的 `horn` | 主峰高度和尖度 | 指数 1.02 接近纯金字塔；调大变圆 |
| `envelope` 的 `range` | 两侧群峰，每行一座 | 改位置和宽度即可重排山脊 |
| `terrain` 的 `r` 系数 | 棱线起伏 | `.72` 调大更破碎 |
| `shade` 的 `snow` | 雪线 | `.40,.58` 调低雪更多 |
| `n=normalize(mix(fine,soft,snow*.86))` | 雪面平滑度 | 这是去掉“塑料皱纹”的关键，别删 |
| `FOCAL` | 焦距 | 横纵必须同值，否则山被拉成针状 |
| `t+=max(.012,d*.26)` | 步进精度 | 步子大了陡坡出现台阶瑕疵 |

## 配置 `src/config/scene.config.json`

| 字段 | 含义 | 边界 |
| --- | --- | --- |
| `horizon` | 地平线高度，占窗口比例 | 改动时同步 CSS 变量由 `App` 自动完成；0.4 时舞台高度合适 |
| `peakX` | 主峰的屏幕横向位置 | 小于 0.5 会被左侧面板挡住 |
| `terrainHeight` | 贴图显示高度，占窗口高度 | 越小山越远越辽阔；主峰约占贴图 53% |
| `terrainAspect` | 贴图宽高比 | 必须等于烘焙尺寸比 |
| `maxDevicePixelRatio` | 内部分辨率上限 | 1.5 已经足够，调高只增加 GPU 负担 |
| `ripple.maximum` | 同时存在的涟漪 | 必须与着色器 `MAX_RIPPLES` 一致 |
| `ripple.voiceEveryMs` | 说话时投涟漪的间隔 | 太小水面会乱 |
| `ripple.voiceThreshold` | 触发涟漪的最低电平 | 太低环境噪声也起涟漪 |
| `voice.releaseHalfLifeMs` | 停止说话后的回落 | 约 2 秒内归零 |
| `frame.*Fps` | 三档帧率 | 见 `ARCHITECTURE.md` |

每个时段的配色：

| 字段 | 作用 |
| --- | --- |
| `skyTop/skyMid/skyLow/skyHorizon` | 天空四段渐变，从顶到地平线 |
| `ambient` | 天空光，决定山的阴影色 |
| `key` | 主光，决定山的受光色。太亮会过曝成一片白 |
| `haze` | 深度雾和高度雾，让山脚融进地平线 |
| `snow`、`rock` | 雪和岩石的底色 |
| `waterDeep`、`waterShallow` | 水体颜色，近处透出 |
| `glow` | 光晕颜色 |
| `sun` | `[x, y, 强度, 半径]`，光晕位置，水面闪光也集中在它下方 |
| `stars`、`clouds`、`sparkle`、`glint` | 星、云、水面四芒闪光、细光纹的强度 |

## 着色器 `lake.frag.glsl`

- `skyColor`：渐变、光晕、云、银河、三层星、四芒亮星、亮行星、流星。`reflected=true` 时跳过最细的两层星，否则倒影里会出现毛刺。
- `mountain`：读两张贴图，用当前配色打光，加深度雾和贴水面的高度雾。
- `aboveWater`：天空 + 山 + 星座层。湖面倒影复用这个函数，所以倒影永远和上方一致。
- `water`：透视映射、细浪、涟漪、倒影、截图倒影、菲涅尔混合、四芒闪光、细光纹。

几个不能随手改的点：

- `calm = smoothstep(0,.16,below)`：地平线附近保持镜面。去掉后这里的放大扰动会变成抖动的碎线。
- `toWorld`：涟漪在世界坐标里是正圆，投到屏幕才是正确的透视椭圆。不要改成屏幕空间。
- 末尾的 `dither`：消除大面积渐变的色带，去掉后暗部会出现明显条纹。

## 星座 `src/config/constellation.json`

`stars` 按点亮顺序排列，从鲸尾到鲸头，共 23 颗。今天第 N 条记录点亮第 N 颗。超过 23 条的记录散落在周围。`lines` 两端都点亮时才画实线，否则画虚线占位。画布区域在 `SceneRenderer.resize` 里算。
