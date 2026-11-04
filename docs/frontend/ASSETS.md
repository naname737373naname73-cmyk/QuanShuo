# 素材

星湖不需要外部生成素材。海棠的画由 GPT 图片模型生成，原图放在 `frontend/art/crabapple`，不进安装包。此前“春日手绘插画”的素材清单和提示词已作废，不用再做。

| 路径 | 用途 | 来源 | 如何更新 |
| --- | --- | --- | --- |
| `public/assets/lake/terrain-a.png`、`terrain-b.png` | 远山光照数据，4320×720 | `tools/bake/terrain.html` 程序生成 | `node tools/bake/bake-terrain.mjs --w 4320 --h 720 --aa 3` |
| `public/assets/lake/fallback-{dawn,noon,dusk,night}.jpg` | WebGL 不可用时的静态图 | 当前场景截图 | `node tools/bake/bake-fallback.mjs` |
| `src/QuanShuo.Native/Assets/Starlake/night.png`、`dusk.png` | 星湖语音框的玻璃，1120×560，窗口按大小拉伸 | 自己的湖景加日历同款玻璃 | `node tools/bake-voice-glass.mjs` |
| `art/crabapple/roundel-pink.png` | 海棠主画原图，1254×1254，花和花之间留了空隙才切得干净 | GPT 生成 | 换画后改 `tools/bake-crabapple.mjs` 里的种子位置 |
| `public/assets/crabapple/colour.webp`、`ink.webp`、`flowers.webp` | 上色的画、淡墨稿、切下来的花 | 由原图烘焙 | `node tools/bake-crabapple.mjs`，同时更新 `src/config/crabapple-flowers.json` |
| `art/crabapple/roundel-blue.png` | 水边那幅原图，1254×1254，花开满后点空白处换成它。照粉色那幅的画风、桌面双圆稿右边蓝圆的题材重画 | GPT 生成 | 换画后改 `tools/bake-crabapple.mjs` 里的 `BLUE_SEEDS` |
| `public/assets/crabapple/colour-blue.webp` | 水边那幅 | 由原图烘焙 | `node tools/bake-crabapple.mjs blue`，同时更新 `src/config/crabapple-flowers-blue.json` |
| `public/assets/crabapple/print.webp`、`src/QuanShuo.Native/Assets/Crabapple/print.jpg` | 海棠的印花：淡墨稿用开出来的花的颜色 `#C97F6F` 六成浓度印在 `#F5EEE2` 上。日历四周、录音卡片和说话窗口的底都是它，上面贴题签写字 | 由 `ink.webp` 烘焙 | `node tools/bake-crabapple-print.mjs`；从原图哪里取写在 `styles/crabapple.css`、`RecordingCard.cs`、`SpeechWindow.cs` 的 `PrintOrigin` |
| `art/crabapple/calendar-bg.png`、`card-bg.png`、`speech-bg.png` | 2026-11-03 以前的彩色花框，已不用，只留原图 | GPT 生成 | 不进安装包 |
| `public/assets/crabapple/mark-{bud,half,bloom,cluster}.svg` | 日历上的四档花 | 手写 SVG | 直接编辑 |
| `public/assets/brand/logo.svg` | 标志，用 `currentColor` | 手写 SVG | 直接编辑 |
| `public/assets/brand/logo-mono.svg` | 小尺寸单色标志 | 手写 SVG | 直接编辑 |
| `public/assets/brand/logo-app.svg` | 应用图标，512 网格 | 手写 SVG | 直接编辑后运行 `node tools/generate-icon.mjs` |
| `public/assets/brand/icons.svg` | 32 个界面图标，`<symbol id="i-名字">` | 手写 SVG | 直接编辑；引用见 `components/icons.ts` |
| `public/assets/brand/empty-lake.svg` | 空状态线稿 | 手写 SVG | 直接编辑 |
| `public/assets/brand/whale-constellation.svg` | 检查鲸形状用，运行时不加载 | 手写 SVG | 与 `constellation.json` 同步 |
| `src/config/constellation.json` | 鲸的星座数据 | 手写 | 见 `SCENE.md` |
| `public/assets/fonts/noto-serif-sc-subset.woff2` | 标题字体子集 | 系统 Noto Serif SC，SIL OFL 1.1 | `node tools/subset-font.mjs` |

参考图（雪山星空照片、水面少女壁纸）只用于确定气质，没有任何像素进入应用。不要把它们放进仓库。
