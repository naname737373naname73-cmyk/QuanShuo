# 设计令牌

权威来源是 `frontend/src/styles/tokens.css`。原生层对应 `src/QuanShuo.Native/Shell/NativeTheme.cs`，改色时两边同步。

## 主题

星湖现在只有傍晚和夜晚，都用深色令牌。浅色令牌留着，界面上走不到。

| 令牌 | 深色 | 浅色 | 用在哪里 |
| --- | --- | --- | --- |
| `--glass` | `rgba(9,14,40,.56)` | `rgba(255,255,255,.50)` | 日记和检视器面板 |
| `--glass-strong` | `rgba(11,17,48,.84)` | `rgba(250,253,255,.86)` | 设置、提示条 |
| `--glass-soft` | `rgba(190,210,255,.07)` | `rgba(17,40,110,.05)` | 输入框、悬停底 |
| `--glass-hover` | `rgba(190,210,255,.10)` | `rgba(17,40,110,.08)` | 按钮悬停 |
| `--highlight` | `rgba(235,243,255,.16)` | `rgba(255,255,255,.70)` | 面板顶部内高光 |
| `--ink` | `#eef3ff` | `#101f50` | 正文 |
| `--ink-muted` | `#b3bfe0` | `#3d4f7e` | 次要文字 |
| `--ink-faint` | `#8796c0` | `#5d6f9b` | 第三层文字 |
| `--line` | `rgba(190,210,255,.16)` | `rgba(20,50,125,.14)` | 发丝线 |
| `--line-strong` | `rgba(190,210,255,.30)` | `rgba(20,50,125,.28)` | 输入框悬停、图片外框 |
| `--accent` | `#a9eaf2` | `#2344a8` | 主按钮、选中线、开关 |
| `--accent-ink` | `#0b1230` | `#ffffff` | 主按钮文字 |
| `--accent-soft` | `rgba(169,234,242,.14)` | `rgba(35,68,168,.10)` | 选中行底色 |
| `--warm-text` | `#ffc9a3` | `#b4562a` | 星数、录音中、转写未完成 |
| `--danger-text` | `#ffb1a9` | `#b23a30` | 错误 |

暖色是点睛，单个界面面积不超过 2%。

海棠只有一套浅色令牌，写在 `frontend/src/styles/crabapple.css`，不随时段变化。纸色 `#f6eedd`，正文 `#3a2314`，强调色是海棠花苞的 `#95384c`，金线 `#bf9440`，圆角收到 3–6 px。小字 `--ink-faint` 在纸色上的对比度是 5.5。原生层对应 `NativeTheme` 的语义色。

## 排版

| 令牌 | 值 | 用在哪里 |
| --- | --- | --- |
| `--font-display` | QS Serif（Noto Serif SC 子集） | 标题和“N 颗星” |
| `--font-body` | Segoe UI Variable Text、Microsoft YaHei UI | 界面文字 |
| `--font-mono` | Cascadia Mono、Consolas | 时间、路径、快捷键 |
| `--text-2xs` 到 `--text-hero` | 11、12、13、14、16、20、26、54 | 见 `tokens.css` |

**子集字体只含固定标题里的字。** 改了 `tools/subset-font.mjs` 列表之外的标题文案，要加进去并运行 `node tools/subset-font.mjs`，否则缺字会回退成别的字体。用户内容不走这个字体。

## 布局

| 令牌 | 值 | 说明 |
| --- | --- | --- |
| `--titlebar-height` | 44px | 自绘标题栏 |
| `--journal-width` | 368px | 左侧日记。改动时同步 `SceneRenderer.resize` 里星座区域的 `left` |
| `--inspector-width` | 324px | 右侧检视器 |
| `--edge` | 12px | 面板到窗口边 |
| `--record-height` | 92px | 列表行高，虚拟列表读取它 |
| `--date-height` | 40px | 日期行高 |
| `--horizon` | 由 `scene.config.json` 写入 | 舞台底边贴着地平线 |
| `--radius-sm/md/lg` | 8、14、20 | 控件、卡片、大面板 |
| `--blur-panel` | 30px | 磨砂半径 |

## 动效

`--motion-fast` 140ms，`--motion-normal` 240ms，`--motion-slow` 460ms，`--motion-scene` 900ms，`--ease-out` 是 `cubic-bezier(.16,1,.3,1)`。
