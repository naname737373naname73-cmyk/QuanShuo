# 界面层架构

设计依据见 `STARLAKE_BRIEF.md`。

## 模块边界

- `app/App.ts`：唯一组装点。持有记录、搜索词和选择集合，不参与绘制。
- `components/RecordList.ts`：虚拟列表。滚动时复用已挂载节点，缩略图按记录 ID 缓存。
- `components/StageView.ts`：舞台和检视器。负责截图悬浮、倒影纹理、文字编辑、保存、重新转写、删除。
- `components/SettingsView.ts`、`OnboardingView.ts`、`DebugPanel.ts`：设置长卷、首次运行、调试面板。
- `bridge/`：`NativeBridge` 和 `MockBridge` 实现同一接口，组件不知道自己跑在 WebView2 还是浏览器。
- `app/skin.ts`：两套皮肤的文案。皮肤在第一帧之前由 URL 的 `skin` 参数定下，写到 `<html data-skin>`。
- `scene/Scene.ts`：主画面的接口。业务层只认它，星湖和海棠各有一个实现。
- `scene/BloomScene.ts`：海棠的主画面。2D 画布，平时没有动画循环，只在开花、转彩和飘花瓣时才跑。
- `scene/SceneRenderer.ts`：星湖的场景调度。管时钟、时段、声音、帧率、涟漪触发、星座命中。
- `scene/LakeScene.ts`：WebGL2 封装。上传纹理和 uniform，不持有业务数据。
- `scene/ConstellationLayer.ts`：把今天的记录画到一张 2D 画布，交给湖景当纹理，所以星座也有倒影。
- `scene/shaders/lake.frag.glsl`：一个片元着色器完成天空、星、远山打光和湖面。

## 数据流

```text
Native/Mock → records.query → App.records
                              ├→ RecordList
                              ├→ “今天亮起 N 颗星”
                              └→ SceneRenderer.setRecords → 今天的记录按时间正序点亮星座

列表悬停 ⇄ 星星悬停（SceneRenderer.setHovered / starhover）
列表或星星点击 → App.select → StageView.show → SceneRenderer.setStageImage（倒影）
编辑 → drafts（按记录 ID）→ 防抖 550ms → 串行保存 → updated → 刷新列表
复制给 AI → clipboard.copy → SceneRenderer.celebrate（流星 + 涟漪）
recording.level → SceneRenderer.setMicrophoneLevel → 细浪和周期涟漪
transcript.delta → App.showListening → “正在听”面板
records.created → loadRecords(animate=true) → 新星闪耀 + 涟漪
```

`records.created` 才播放新星动画，`records.changed` 只刷新。

## 文字保存

`StageView` 的草稿放在 `Map<记录ID, 草稿>` 里，保存在一条 Promise 队列上串行执行。三条规则不能破坏：

- 保存回调不能使用“当前记录”。用户可能已经切到另一条，否则下一次保存会把 B 的文字写进 A。
- 保存失败时草稿留在 Map 里，切回这条还能看到，下次还会重试。
- 复制、删除、重新转写、关窗之前都要 `await stage.settle()`。原生层按磁盘内容生成剪贴板，不等落盘就会复制旧文字。

`App` 同时持有 `allRecords` 和 `records`。搜索只影响列表；星座、选择和复制都按 `allRecords` 解析，否则点星星会选中一条“不存在”的记录。

回归测试：`npm run test:drafts`。

## 每一帧的顺序

1. 从 `SceneClock` 取秒数。`delta` 上限 100ms。
2. 时段向目标值连续插值；固定 `t` 时直接等于目标值。
3. 声音电平按起音和释音半衰期平滑（`config.voice`），超过阈值时周期性投涟漪。
4. 倒影透明度向目标值推进。
5. 星座层有变化或在播放闪耀动画时重绘并上传纹理；否则跳过，避免每帧上传。
6. `LakeScene.render`：按 `periodBlend` 插值全部配色，上传 uniform，绘制全屏三角形。
7. DOM 始终在画布上方。文字只出现在磨砂面板内或首次运行的暗化区内。

## 渲染生命周期

- 有交互、涟漪、声音或时段过渡：60fps。
- 平时：30fps。星空几乎不动，没有必要满帧，省电。
- 失焦：20fps。
- `document.hidden`：取消 RAF，不绘制。
- 安静模式、系统减少动画：取消 RAF，状态变化时只画一帧。
- URL 有 `t`：固定时钟，只画一帧，用于可复现截图。
- 持续掉帧：`adaptResolution` 把内部分辨率每次降 20%，下限 50%。
- WebGL2 初始化失败：显示 `assets/lake/fallback-{时段}.jpg`，星座画布直接盖在上面，功能不受影响。
- 上下文丢失（显卡驱动重置）：切到回退图，恢复后重新初始化并重传倒影。

星湖只有傍晚和夜晚：`day` 在 2 和 3 之间。`dayFromClock` 按系统时间给出连续值，窗口每分钟跟一次；用户拖过滑块后，这次窗口不再跟随，并通过 `scene.day` 告诉原生层，语音框才跟得上。

所有视觉随机量来自着色器内的哈希或 `seededRandom`。`Math.random()` 只用于流星种子和声音涟漪的位置，不影响可复现截图（固定时钟下没有声音涟漪和流星）。

## 状态流转

```text
启动 → 加载中 → 正常 / 空记录
正常 → 搜索无结果 ──清空搜索──→ 正常
正常 → 单选（舞台打开）→ 编辑 / 重新转写 / 删除 / 再点同一条收起 → 正常
正常 → Ctrl/Shift 多选（截图堆叠）→ 复制合并文本 → 正常
录音事件到来 → “正在听”面板 → 结束后自动收起
首次运行 → 说明 → 可跳过 Key → 试截图 → 正常
设置 → 保存 → 更新安静模式、时段、快捷键提示
```

## WebView2 生命周期

历史窗口需要时才创建，关闭即销毁 WebView2，不常驻。托盘、快捷键、覆盖层、录音卡片都不依赖它。新实例打开时按当前设置构造 URL：`?state=...&day=...&quiet=1`。
