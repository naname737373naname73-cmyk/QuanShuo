# 圈说前端交接

这份文件是精修界面的唯一入口。不需要读过需求原文，也不需要参与过开发。先按下一节跑起来，再按“想改 X 去哪里”定位文件。

## 1. 只跑界面（不需要 .NET、不需要 C#）

环境：Windows、macOS 或 Linux；Node.js 24；Chrome 或 Edge。

```bash
cd frontend
npm ci
npm run dev
```

打开 `http://127.0.0.1:5173/`。浏览器没有原生桥接时自动使用 `MockBridge`，内置记录和缩略图均由代码生成，可以直接编辑、搜索、多选、复制、删除并撤销。模拟阿里云的“测试连接”不代表真实云服务已连通。

生产构建：

```bash
npm run build
```

文字保存回归测试（改 `StageView` 或 `App` 的选择逻辑后必须跑）：

```bash
npm run test:drafts
```

安装器回归测试（改 `src/QuanShuo.Installer` 后必须跑）。安装器的问题编译发现不了，曾经点“浏览”就永久卡死：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-installer.ps1
```

边说边改的回归测试（改 `LiveTranscript`、`LiveTextBox`、`RecordingSurface` 或协调器后必须跑）：

```powershell
QuanShuo.exe --smoke-live --smoke-output <结果文件>
```

用真实的系统编辑框走一遍退格、改前文、挪光标、组字、选区和纯语音记录的文件往返，不联网。

`--smoke-speech` 用真窗口走完整流程，包括贴进另一个进程的窗口，需要传 `--smoke-target <窗口句柄> --smoke-target-file <目标写出的文件> --smoke-shots <截图目录>`，加 `--smoke-input <wav>` 时再用真实识别跑一轮。**必须在 100% 和 150% 两块屏上各跑一次**：字体释放的错误只在 150% 屏上出现。

自动基线截图：

```bash
npm run shots
```

脚本会自行在 `127.0.0.1:5174` 启动 Vite，优先使用本机 Chrome/Edge；没有系统浏览器时先运行 `npx playwright install chromium`。可用 `CHROME_PATH` 指定浏览器。单张预览用 `npm run shots -- --preview`。

## 2. 目录地图

```text
frontend/
  src/
    app/                 应用组装、URL 状态与查询参数、两套皮肤的文案
    bridge/              原生桥接、浏览器模拟桥接、记录/设置类型
    components/          虚拟列表、舞台检视器、设置、首次运行、调试面板
    config/              scene.config.json、constellation.json、crabapple-flowers.json
    scene/               主画面接口；星湖的时钟、湖景、星座、调度；海棠的 BloomScene
      shaders/           lake.vert.glsl、lake.frag.glsl
    styles/              tokens、base、history、settings、calendar、crabapple
    utils/               DOM、日期、复制文本格式
  public/assets/
    lake/                烘焙的远山数据和回退图
    brand/               标志、图标、空状态线稿
    crabapple/           海棠的画、切下来的花、印花和花记号
    fonts/               标题字体子集
  art/crabapple/         海棠的原图，不进安装包
  tools/
    bake/                地形烘焙、回退图、预览、测量
    bake-crabapple.mjs   从原图生成淡墨稿和花
    subset-font.mjs      裁剪标题字体
  scripts/shots.mjs      基线截图、序列帧和测量
src/QuanShuo.Core/       文件格式、记录模型、复制格式、设置模型
src/QuanShuo.Native/     快捷键、截图、录音、转写、索引、Windows 宿主
src/QuanShuo.Installer/  安装与卸载。主题和按钮链接自 Native，夜景图取自 public/assets/lake
tools/test-installer.ps1 安装器端到端测试
docs/frontend/           本套交接文档、基线截图和序列帧
```

## 3. 当前完成度

设计依据是 `STARLAKE_BRIEF.md`：一片高山镜湖，一条记录是一颗星，今天的记录连成一头鲸。

### 已实现

- 程序生成的星湖场景：傍晚和夜晚连续打光、星空、银河、雪山、镜面倒影、透视涟漪、四芒闪光、流星。
- 选中的截图悬在湖面上并投下倒影。说话时水面起涟漪。今天的记录点亮鲸的星座。
- 历史界面：日记列表、舞台、检视器、搜索、多选、复制、删除撤销、设置长卷、首次运行。
- 自绘标题栏，时段滑块可拖动。
- 安静模式、系统减少动画、三档帧率、隐藏停止绘制、持续掉帧自动降分辨率、WebGL 回退。
- 固定时间和种子的 URL 截图，`npm run shots` 基线脚本。
- 原生圈选覆盖层、录音卡片、托盘图标、窗口外壳、安装器按同一设计重做。
- 录音时可以随意改文字，说话悬浮窗（Ctrl+Alt+H），历史列表可收起（Ctrl+B）。

### 真机验证（2026-11-01，RTX 5060 Ti，4K@150% + 2K@100% 双屏）

- 历史窗口：无边框、自绘标题栏、最大化和还原、二次启动唤起。
- 圈选：矩形、尺寸标签、工具条、Tab 切换、自由圈选、Space 开始录音、Enter 保存。开着中文输入法测试通过。
- 录音卡片：声纹涟漪、未填 Key 的提示、结束后的失败态。
- 安装器：浏览、安装、覆盖升级、运行中拦截、拒绝错误位置卸载、保留用户文件的卸载，100% 和 150% 缩放。
- 占用：历史窗口打开空闲时 CPU 2.1%、3D GPU 1.2%、私有内存 283MB（含 6 个 WebView2 进程）；关闭后 CPU 0%、GPU 0%、18MB，WebView2 进程全部退出。

原生层用 `QUANSHUO_TRACE_FILE=<路径>` 打开诊断日志，会记录消息类型和按键。

**用脚本测试时注意**：PowerShell 5.1 按 GBK 读无 BOM 的脚本，中文窗口标题会匹配失败。自动测试 Windows 窗口时用 `[char]0x5708` 这样的码点，并在找不到窗口时立即中止。

2026-11-01 由 GPT 6 astra 做过一轮独立代码审查，发现 12 条缺陷，全部修复。其中 3 条高危都是丢字或写错记录。

### 已知问题

- “更多复制”菜单还是深色的星湖样式，托盘菜单共用同一套，所以没有跟着录音卡片换皮肤。
- 2026-11-01 有一条 42 秒的录音（`171654`）状态是已完成，文字只有一个“。”。当时走代理，原因没有查实：可能是线路问题，也可能是用户自己改过文字。`completed` 的记录没有“重新转写”按钮。修完直连后再出现才值得查。
- 记录 ID 只有时分秒，历史索引又以 ID 为主键。不同日期、同一时分秒的两条记录文件都在，列表只显示其中一条。2026-11-01 实测确认，未修。修法是让 ID 带上日期，`records.query` 之外都把 ID 当不透明字符串用，索引同步时会自动换成新 ID。

### 尚未验收

- 真人说话时的识别质量。2026-11-01 用真实 Key 和合成语音跑通了转写、重新转写、暂停保活、长录音、错误提示和边说边改，但没有用真麦克风说过话。
- 真实中文输入法组字时收到识别结果。测试只模拟了组字消息。星湖的文字压在玻璃上之后也没有用真实输入法试过。
- 拖动语音框时下面的圆角层跟手不跟手，只测了程序移动，没有人手拖过。
- 贴回微信、浏览器、终端等具体程序。测试用的目标是一个普通文字框。
- 快捷键与各程序的输入冲突实测。
- 未签名安装版、便携版的实机 SmartScreen 和杀毒表现。
- Windows 10 上的圆角和阴影退回逻辑。
- 核显上磨砂面板的合成开销。

## 4. 想改 X 去哪里

| 想改的内容 | 文件/入口 |
| --- | --- |
| 面板宽度、字号、磨砂、配色 | `frontend/src/styles/tokens.css`，说明见 `DESIGN_TOKENS.md` |
| 海棠的配色和组件观感 | `frontend/src/styles/crabapple.css`，只写 `[data-skin="crabapple"]` 下的覆盖 |
| 两套皮肤的说法 | `frontend/src/app/skin.ts` |
| 星湖语音框的玻璃 | 改 `frontend/tools/bake-voice-glass.mjs` 后 `node tools/bake-voice-glass.mjs`，产出 `src/QuanShuo.Native/Assets/Starlake` |
| 星湖语音框的边框、星尘、月亮 | `src/QuanShuo.Native/Recording/StarlakeGlass.cs`，圆角和阴影在 `CardHalo.cs` |
| 傍晚和夜晚的分界 | `frontend/src/app/query.ts` 的 `CLOCK_KEYS` 和 `NativeTheme.IsDuskAt`，两边要一起改 |
| 花开的顺序、动画、花瓣 | `scene/BloomScene.ts`；顺序在 `tools/bake-crabapple.mjs` 的种子表 |
| 开满后换成水边那幅 | `scene/BloomScene.ts` 的 `swapPainting`；那幅画里每朵花的位置在 `tools/bake-crabapple.mjs` 的 `BLUE_SEEDS`，改后 `node tools/bake-crabapple.mjs blue` |
| 换皮肤的流程 | `App.ts` 的 `switchSkin`，保存前的检查在 `SettingsView.ts`，原生层在 `HistoryWindow.cs` 的 `settings.update` |
| 列表行 | `tokens.css` 的 `--record-height`、`components/RecordList.ts`、`styles/history.css` |
| 搜索、选择、复制、提示条 | `frontend/src/app/App.ts` |
| 文字保存与草稿 | `components/StageView.ts`，规则见 `ARCHITECTURE.md` |
| 舞台、检视器 | `components/StageView.ts`、`styles/history.css` |
| 设置字段和测试连接 | `components/SettingsView.ts`；桥接契约见 `BRIDGE.md` |
| 首次运行文案 | `components/OnboardingView.ts`；新增标题字要重跑字体子集 |
| 示例记录和模拟错误状态 | `bridge/mockBridge.ts`、`app/query.ts` |
| 时段配色、地平线、主峰位置 | `src/config/scene.config.json`，说明见 `SCENE.md` |
| 天空、水面着色 | `scene/shaders/lake.frag.glsl` |
| 山的形状和雪线 | `tools/bake/terrain.html`，改完重新烘焙 |
| 星座形状 | `src/config/constellation.json` |
| 星座的绘制和命中 | `scene/ConstellationLayer.ts` |
| 涟漪、声音、帧率调度 | `scene/SceneRenderer.ts` |
| 动效时长 | `styles/tokens.css`、`MOTION.md` |
| 图标、标志 | `public/assets/brand/` |
| 截图状态和输出 | `frontend/scripts/shots.mjs`、`STATES.md` |
| 原生/前端消息 | 两侧 `bridge/`、`docs/frontend/BRIDGE.md` |
| 原生颜色、字体、皮肤 | `src/QuanShuo.Native/Shell/NativeTheme.cs`；窗口用 `Surface`、`Text`、`Accent` 这些语义色，不直接用 `Midnight`、`Cream` |
| 圈选覆盖层 | `src/QuanShuo.Native/Capture/OverlayForm.cs`、`StarTrail.cs` |
| 录音卡片 | `src/QuanShuo.Native/Recording/RecordingCard.cs` |
| 说话悬浮窗 | `Recording/SpeechWindow.cs`；和卡片共用的外壳、涟漪、保存在 `RecordingSurface.cs` |
| 贴回其他程序 | `Recording/PasteService.cs` |
| 识别结果和用户修改的合并 | `src/QuanShuo.Core/Transcription/LiveTranscript.cs`、`Recording/LiveTextBox.cs` |
| 列表收起 | `App.ts` 的 `setJournalOpen`、`history.css` 的 `--journal-offset` |
| 托盘和应用图标 | `Shell/TrayIconService.cs`、`tools/generate-icon.mjs` |
| 窗口外壳和标题栏 | `src/QuanShuo.Native/History/HistoryWindow.cs` |
| PNG、Markdown、同秒命名 | `src/QuanShuo.Core/Storage/` |
| 快捷键注册与冲突处理 | `src/QuanShuo.Native/Input/GlobalHotkeyService.cs` |

## 5. URL 参数（全部可直接组合）

| 参数 | 取值与默认 | 用途 |
| --- | --- | --- |
| `skin` | `starlake`（默认）、`crabapple` | 皮肤 |
| `state` | `normal`（默认）、`empty`、`loading`、`hover`、`selected`、`recording`、`failed`、`search-empty`、`detail`、`settings`、`onboarding` | 直接进入一个界面状态 |
| `records` | `0`–`5000`，默认 `5` | 模拟记录数；`state=empty` 强制为空 |
| `day` | `2` 傍晚、`3` 夜晚，小数是两者之间；默认按系统时间 | 固定时段 |
| `t` | 非负秒数，如 `12.5` | 出现即冻结动画时钟；不写则实时运行 |
| `seed` | 整数，默认 `1` | 保留，供确定性调试 |
| `wind` | `0`–`1` | 强制声音电平，用来模拟说话 |
| `quiet` | `1` | 安静模式，停用全部动效 |
| `webgl` | `0` | 强制静态图回退 |
| `debug` | `1` | 初始打开调试面板 |

可复现示例：

```text
http://127.0.0.1:5173/?state=selected&day=3&records=8&t=12.5&seed=1
http://127.0.0.1:5173/?state=recording&day=2&records=8&t=20&seed=1&wind=0.85
http://127.0.0.1:5173/?state=normal&day=1&quiet=1&webgl=0
```

## 6. 调试与性能观察

按 Ctrl+Shift+D：调时段、声音、固定时间，投石子，放流星，看 FPS。

`node tools/bake/measure.mjs` 量一帧的 GPU 耗时。2026-11-01 在 RTX 5060 Ti 上：1366×768 约 0.8ms，1600×1000 约 0.8ms，2560×1440 约 1.1ms，3840×2160 约 1.9ms。这是无头 Chrome 的数据，只含场景本身，不含磨砂面板的合成开销。

注意 `gl.finish()` 在 Chrome 里不等 GPU，用它测出来是 0ms。必须用 `readPixels` 强制同步。

## 7. 阅读顺序

1. `ARCHITECTURE.md`：知道数据、渲染和 WebView2 生命周期。
2. `DESIGN_TOKENS.md` 与 `SCENE.md`：先改配置，再改逻辑。
3. `MOTION.md` 与 `STATES.md`：改动效和状态前看基线。
4. `BRIDGE.md`：新增消息必须同时更新类型、模拟桥、原生宿主和此文档。
5. `ASSETS.md`：所有素材的来源和更新方式。
6. `DECISIONS.md` 与 `CHANGELOG.md`：记录不满意的地方和具体参数变化，别覆盖历史判断。
