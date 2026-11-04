# 原生层与界面层消息

## 信封与规则

请求（前端 → 原生）：

```json
{ "id": "web-1", "type": "records.query", "payload": { "query": "按钮" } }
```

成功响应（原生 → 前端）：`{ "id": "web-1", "payload": [] }`。
失败响应：`{ "id": "web-1", "error": "人能理解的原因和下一步" }`。
原生事件没有 `id`：`{ "type": "records.changed", "payload": {} }`。

所有字段使用 camelCase。时间带时区偏移；`status` 为 `not-requested | recording | completed | incomplete`。错误文本不能含 API Key、HTTP Authorization 头或堆栈中的密钥。

`QuanShuoRecord`：

```json
{
  "id": "143022",
  "timestamp": "2026-10-31T14:30:22+08:00",
  "appName": "Figma",
  "windowTitle": "Spring scene",
  "imagePath": "C:\\Data\\2026-10-31\\143022.png",
  "markdownPath": "C:\\Data\\2026-10-31\\143022.md",
  "audioPath": "C:\\Data\\2026-10-31\\143022.wav",
  "text": "这个按钮的间距太大了。",
  "status": "completed"
}
```

`audioPath` 可为 `null`/省略。`imagePath` 为空字符串表示只说话、没有截图的记录，这时不要请求 `record.thumbnail`、`record.image` 和 `record.drag`。浏览器模拟记录另有 `imageUrl`；原生记录不返回这个字段。

## 查询与图片

### `records.query`

- 方向：前端 → 原生；触发：打开历史、搜索、文件变化后。
- 请求：`{ "query": "可选，空白表示全部" }`。
- 响应：`QuanShuoRecord[]`，日期/时间倒序，最多 5000 条。
- 错误：索引损坏时可从 Markdown 重建；目录不可读时返回错误，历史继续显示旧数据并就地提示。

### `record.thumbnail`

- 请求：`{ "id": "143022", "imagePath": "C:\\...png", "width": 240 }`。
- 响应：PNG/JPEG data URL 字符串。按 ID 和宽度缓存；缩放只用于显示，不改原图。
- 错误：文件被手动删除时返回错误，该缩略图显示占位说明，下一次同步删除索引。

### `record.image`

- 请求字段同 `record.thumbnail`；响应用于详情的 data URL。
- 错误：格式不支持或文件缺失时返回错误，详情保留文字和文件路径。

### `record.drag`

- 请求：`{ "id": "143022", "imagePath": "C:\\...png" }`；触发：缩略图开始拖动。
- 响应：`null`。原生以 `CF_HDROP`/文件拖放形式交给目标窗口。
- 错误：文件不存在时返回错误，不创建空拖放。

## 编辑、删除与转写

### `record.update`

- 请求：`{ "id": "143022", "markdownPath": "C:\\...md", "text": "编辑后的原话" }`。
- 响应：`null`；保存采用临时文件再改名。
- 错误：只读目录、文件占用或磁盘满时返回错误，编辑器内容不能清空。

### `record.delete`

- 请求：`{ "id": "143022" }`；PNG/MD/WAV 全部进回收站。
- 响应：`null`；随后发送 `records.changed`。
- 错误：任一文件失败时说明哪个文件未删除，不能假装成功。

### `records.delete`

批量移入回收站，请求 `{ ids: string[] }`。整批算一次操作，`record.undo` 传其中任意一个 ID 就全部恢复。列表里有正在录音的记录时整批拒绝。

### `record.undo`

- 请求：`{ "id": "143022" }`；响应：恢复的 `QuanShuoRecord` 或 `null`。
- 错误：回收站项目已被外部清空时说明无法恢复。

### `record.retry`

- 请求：`{ "id": "143022" }`；触发：保留 WAV 的失败记录。
- 响应：更新后的 `QuanShuoRecord`。
- 错误：无 Key、WAV 缺失、断网、服务端错误都保留原文件与已有文字，状态回到 `incomplete`。

## 复制

### `clipboard.copy`

- 请求：

```json
{ "operation": "ai", "ids": ["143022", "143315"], "text": "浏览器模拟桥接使用的预格式化文本" }
```

- `operation`：`ai | image-path | markdown-path | text | image`。
- 响应：`null`。原生必须以磁盘记录重新生成内容，`text` 只是模拟桥接的便利字段，不能作为可信数据源。
- `ai` 多条按选择顺序编号；`text` 多条按选择顺序用空行隔开，跳过没有文字的记录，全都没有文字时报错且不动剪贴板；`image` 同时提供位图和文件路径。其余操作只用第一条。
- 错误：剪贴板被占用时返回“稍后重试”，不能改写其他应用数据。

## 设置与系统

### `settings.get`

- 请求：`{}`；响应：`AppSettings`，不包含 API Key。

```json
{
  "hotkey": "Ctrl+Alt+G",
  "speechHotkey": "Ctrl+Alt+H",
  "dataDirectory": "C:\\Users\\Example\\Documents\\QuanShuo",
  "freeformFillColor": "#FFFFFF",
  "deleteAudioAfterSuccess": true,
  "autoStart": true,
  "quietMode": false,
  "autoSpeakAfterSelection": false,
  "speechModel": "qwen-audio-3.1-asr-flash-message",
  "speechProvider": "dashscope",
  "localSpeechEndpoint": "ws://127.0.0.1:8765/asr",
  "localSpeechModel": "default",
  "speechEndpoint": "wss://dashscope.aliyuncs.com/api-ws/v1/inference",
  "speechLanguage": "zh",
  "microphoneDeviceId": "0",
  "vocabularyId": null,
  "fixedDayPeriod": null,
  "skin": "starlake"
}
```

### `settings.update`

`skin` 只能是 `starlake` 或 `crabapple`。原生层保存后立刻换圈选、录音、菜单和托盘的配色；前端收到后把搜索词和选择记进 sessionStorage，带着新的 `skin` 参数重开页面。换皮肤之前前端要先等文字落盘，保存失败时不发这条消息。

- 请求：完整 `AppSettings`；响应：保存后的对象。
- `settingsRevision` 为原生维护的版本号，旧设置默认 0。请求须带上最近读取/保存返回的版本，原生保存时递增；过期版本拒绝写入。保存、切换和首次设置完成共用跨窗口互斥锁，防止旧窗口覆盖新选择。
- `speechProvider` 为 `dashscope`（旧设置默认）或 `local`。本地地址仅允许回环 ws/wss，模型/配置名称非空。两个来源的模型和地址分别保存；一次录音使用开始时的配置。
- 错误：阈值自动限制在 200–800ms；无效目录/快捷键、两个快捷键相同都返回错误，不写坏旧配置。

### `secret.set`

- 请求：`{ "apiKey": "sk-...；空字符串表示删除" }`；响应：`null`。
- 仅 DPAPI 当前用户加密保存。该消息永远不出现在日志中。

### `audio.devices`

- 请求：`{}`；响应：`[{ "index": 0, "name": "麦克风名称" }]`。
- 无设备返回空数组，设置仍可保存。

### `connection.test`

- 请求：`{ "settings": <当次保存的 AppSettings> }`；兼容旧请求 `{}`，省略时使用当前设置。响应：`{ "ok": true, "message": "连接正常：麦克风可以打开，阿里云已接受 <模型名>。" }`。测试使用请求快照，后续修改设置不会改变已开始测试的识别来源；同一时间只允许一次测试。
- 原生打开麦克风约 250ms，再验证 Key 和模型：流式模型建立任务但不发送音频，整段模型发送 0.5 秒静音。无 Key、权限拒绝、握手失败、模型不可用都返回可操作错误。
- 本地来源不打开麦克风、不读取云端 Key；发送 0.5 秒 PCM 静音并等待协议完成，明确提示这不是准确率/性能验证。录音中拒绝连接测试。

### `localAsr.modelInfo` / `localAsr.openModelDocs`

- `localAsr.modelInfo`：无参数，只读取上次测试缓存，返回 `null` 或 `{ endpoint, model, connected, checkedAt, modelInfo }`。`modelInfo` 为 `{ name, runtime?, device?, precision?, estimatedVramMb?, estimatedRamMb? }`，单位 MB。设置页严格按地址和配置 ID 匹配，标注测试时间，不能当成当前运行或实时占用。此请求不连接/启动服务、不加载模型。
- `connection.test` 在本地模式额外返回 `modelStatus`，结构同上，并独立保存缓存；不改写设置来保存模型资料。旧服务没有 `modelInfo` 时，测试照常成功，界面明确显示尚未报告模型。错误时将该配置的最新结果记为未连接。
- `localAsr.openModelDocs` 请求 `{ model: "sherpa" | "qwen" }`，在系统浏览器打开内置白名单中的官方资料，返回 `null`；不接收任意 URL。

### `localAsr.copyAssessmentPrompt` / `localAsr.copySetupPrompt`

- 请求为 `{}` 或 `{ modelId }`；响应均为 `null`。无 ID 时复制原文；固定模型 ID 会附加本次目标及阶段边界，未知 ID 拒绝。只复制提示词，不安装环境、不改设置、不包含密钥。
- `copyAssessmentPrompt` 对应 `docs/local-asr-assessment-prompt.md`：第一阶段只读检查电脑是否适合所选模型，默认用 3–5 句人话给出结论，不安装、不要求交接摘要。
- `copySetupPrompt` 对应 `docs/local-asr-agent-prompt.md`：用户发出第二阶段提示词即授权本次安装、适配、排错和验收。定向提示词限定模型；通用提示词沿用已选目标，没有目标时自行检测并保守选择一个。无需逐步确认；系统变动、影响其他应用、明显超出已约定资源等重要变化才询问。
- 两份提示词是两个独立复制动作，便携包与安装目录也提供各自文件。这是给 agent 的执行约束，应用不会替第三方 agent 拦截安装命令。
- 提示词中的 `--smoke-local-asr` 诊断单独报告 `protocolOk` 与 `streamingObserved`；实时节奏下的非空结果必须在 finish 前到达，快速重转写不作实时判定。应用内“测试连接”仍只验证协议，不能替代真实语音验收。

### `localAsr.profiles` / `localAsr.switchProfile`

- `localAsr.profiles` 无参数，返回三项 `{ id, profile, error }`。只读 `%LOCALAPPDATA%/QuanShuo/local-asr-profiles/<id>.json`；不创建环境、不连接或加载服务。不存在的文件返回空 profile，无效或重复路由返回 error。
- `profile` 为 `{ schemaVersion: 1, id, endpoint, model, expectedModelName }`，固定 ID 为 `paraformer-streaming-int8`、`qwen3-asr-0.6b`、`qwen3-asr-1.7b`。只允许本机 WebSocket 地址，同一 endpoint + model 不得登记为两个模型。
- `localAsr.switchProfile` 请求 `{ id, profile, expectedCurrent: AppSettings }`，返回 `{ settings, modelStatus, message }`。读取及持久化前重新核对登记；当前配置版本变更或录音/重转写/测试忙碌时拒绝。请求目标服务通过静音协议，并核对 ready.modelInfo 的 `name` 和 `catalogId`，匹配后才保存本地连接和新版本。
- 失败保留当前设置；同一网关的不同 ID 会尝试请求原 ID（至多 30 秒），但不以此宣称原模型身份已确认。失败使原身份缓存失效。跨服务显存回收由安装 agent 配置网关/协调器，应用不执行脚本或下载模型。
- 卡片“当前选用”需要已保存路由与成功测试的名称、catalogId 全部相符；只匹配路由时显示“身份待验证”，保留验证入口。模型资料仍是上次测试缓存，不代表服务此刻在运行。

### `system.microphoneSettings`

- 请求：`{}`；响应：`null`；打开 Windows 麦克风隐私设置，不模拟成功授权。

### `capture.test`

- 请求：`{}`；响应：`null`；用备用快捷键路径抓帧并打开原生覆盖层。

### `speech.open`

- 请求：`{}`；响应：`null`；打开说话悬浮窗，已打开时结束并发送，与说话快捷键相同。从历史窗口打开时前台是圈说自己，没有贴回目标，主按钮变成复制。

### `onboarding.complete`

- 请求：`{}`；响应：`null`；只记录首次运行已完成，不要求 Key。

### `window.close`

- 请求：`{}`；响应：`null`；销毁当前 WebView2 和历史窗口，不能只隐藏。

关窗顺序：`window.close` 之前必须等草稿落盘。Alt+F4 和任务栏关闭由原生发 `window.closeRequested`，前端保存后回 `window.close`，保存失败回 `window.keepOpen`。前端 2.5 秒没回应时原生强制关闭，避免页面卡死后窗口关不掉。任务管理器结束和关机不走这个流程。

### `window.keepOpen`

- 请求：`{}`；响应：`null`；取消原生的关窗兜底计时，窗口保持打开。

### `window.minimize`

- 请求：`{}`；响应：`null`；最小化历史窗口。

### `window.toggleMaximize`

- 请求：`{}`；响应：`null`；最大化或还原历史窗口。

## 原生主动事件

| 类型 | payload | 时机 |
| --- | --- | --- |
| `records.created` | 新 `QuanShuoRecord` | 新截图落盘；前端刷新并点亮一颗新星 |
| `records.changed` | `{}` 或变更 ID | 文件修改/删除/撤销/索引同步；只刷新 |
| `recording.level` | `0`–`1` 的数字 | 麦克风 RMS 更新；驱动水面涟漪，不包含音频内容 |
| `transcript.delta` | `{ "id": "143022", "text": "截至目前的原话", "isFinal": false }` | 实时转写更新 |
| `settings.changed` | 不含 Key 的 `AppSettings` | 设置保存或托盘暂停状态改变 |
| `window.state` | `{ "maximized": true }` | 导航完成和窗口状态变化时；切换标题栏的最大化图标 |
| `window.closeRequested` | `{}` | 用户从系统入口关窗；前端保存草稿后决定是否关闭 |
| `view.open` | `{ "state": "settings" }` 或 `"onboarding"` | 窗口已开着时托盘点“设置”等；原生不再重新导航（会丢掉没落盘的草稿），前端就地切过去。页面没加载成功时原生仍然重新导航 |
| `error` | `{ "message": "原因和下一步" }` | 需要就地展示的非致命错误 |

新增或修改消息时必须同时更新：`types.ts`、模拟桥、原生消息处理、本文件。禁止只改一侧字段名。
