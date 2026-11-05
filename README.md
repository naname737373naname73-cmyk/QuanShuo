# 圈说 QuanShuo

按快捷键圈住屏幕，开口补充说明，把图片路径、原话和来源一次复制给 AI。文件保存在本机；云端模式只将音频发往阿里云，本地模式只连接本机识别服务，没有账号、遥测或 AI 改写。

圈说不安装任何全局鼠标/键盘钩子，不拦截右键或其他系统输入；全局入口只有两个可自定义的快捷键：圈选（默认 `Ctrl+Alt+G`，避开 QQ 的 `Ctrl+Alt+A` 截图）和说话（默认 `Ctrl+Alt+H`）。再次启动程序会唤起已运行实例的历史窗口后退出。

## 当前版本

`0.1.1` 开发预览版，面向 Windows 10/11 x64。支持圈选截图、语音转写、静音一分钟自动暂停、历史搜索、编辑、复制，以及转写失败后保留录音重试。提供程序生成的星湖场景和海棠花册两套外观。

“复制给 AI”包含图片的本地路径、文字和来源信息，适合能读取本机文件的桌面 AI Agent。网页版聊天工具通常需要另行上传图片。本地语音模型和运行环境按需安装，不包含在主程序中。

当前仍有双屏混合 DPI、全屏游戏、不同 GPU/驱动和长时间录音等兼容性工作需要持续验证。不同本地模型的资源占用以实际部署为准。问题与建议可提交到 [Issues](https://github.com/naname737373naname73-cmyk/QuanShuo/issues)。

## 安装与启动

从 [GitHub Releases 下载 0.1.0 预览版](https://github.com/naname737373naname73-cmyk/QuanShuo/releases/tag/v0.1.0)：一般用户选择 `QuanShuo-Setup.exe`；希望解压即用时选择 `QuanShuo-portable.zip`。页面上的 `Source code` 是开发源码，不是安装包。

### 便携版

1. 解压 `QuanShuo-portable.zip`。
2. 运行 `QuanShuo.exe`。已自包含 .NET 10 运行时，不需要另装 .NET。
3. 首次运行按三步说明操作；API Key 可以跳过。

### 安装版

运行 `QuanShuo-Setup.exe`，默认安装到 `%LOCALAPPDATA%\Programs\QuanShuo`，不需要管理员权限。安装器为 `src/QuanShuo.Installer` 生成的自包含原生程序，会创建开始菜单快捷方式并注册卸载项；不依赖 Inno Setup。

覆盖安装前请先从托盘退出圈说。卸载在 Windows 设置里进行，只删除圈说自己写入的文件、快捷方式和开机自启，记录和设置都会保留。安装失败的详细原因在 `%TEMP%\QuanShuo-Setup.log`。

历史和设置需要 WebView2 Runtime。若系统没有，窗口内会给出[微软官方安装页](https://developer.microsoft.com/microsoft-edge/webview2/)。覆盖层和录音卡片不依赖 WebView2。

### 未签名程序的提示

当前 exe 没有代码签名证书，Windows SmartScreen 或杀毒软件可能提示“未知发布者”。先核对文件来源和 SHA-256，再决定是否运行；不要因为提示而关闭系统安全防护，也不要运行来路不明的同名文件。

## 第一次使用

1. 在任意程序前按 **Ctrl+Alt+G**（默认快捷键，可在设置修改）。
2. 按住左键拖动圈选，松手完成；Tab 可切换矩形/自由圈选。
3. 按 **Space** 说话，或按 **Enter** 只保存截图。
4. 录音窗口里按 **Space** 暂停，再按一次继续。点击文字或按 **Tab** 后可以编辑，编辑时空格正常输入；点击窗口空白处可回到空格暂停。**Enter** 结束，**Shift+Enter** 换行。
5. 检查文字后点“复制给 AI”。

连续 1 分钟未检测到说话时会自动暂停，窗口显示“静音已暂停”，已有文字和录音保留。点击“继续”，或在没有编辑文字时按空格即可继续；继续后重新计时。检测在本机完成，不需要额外下载模型，也不以识别服务是否及时出字作为判断依据。暂停沿用现有的静音保活连接，并不代表卸载本地模型或释放显存。

只想说话、不要截图时按 **Ctrl+Alt+H**：弹出悬浮窗，边说边改，按 **Enter** 把文字贴回刚才的窗口，文字同时存进历史。贴回去用的是剪贴板，原来的剪贴板内容会被替换。**Esc** 只结束录音，文字留在窗口里。

- **Tab**：矩形/自由圈选切换；**Esc**：取消，不保留覆盖层。
- 托盘菜单：圈选截图、说话、历史记录、设置和退出圈说；使用简洁的纯文字菜单，快捷键显示在右侧，深浅随皮肤切换。双击托盘图标打开历史。
- 皮肤：设置 → 外观与系统。星湖是随时间变化的星空湖面；海棠是手绘花册，每天从淡墨开始，一条记录开一朵花。
- 历史支持搜索、Ctrl/Shift 多选、Ctrl+C 合并复制、编辑、重新转写、删除到回收站和撤销。列表可以收起，按标题栏按钮或 **Ctrl+B**。
- 想一次复制或删除多条时，点列表右上角的勾选图标进入批量选择。可以全选、一键选“未转完”、Shift 连选、Ctrl+A，Esc 退出。
  - “复制给 AI”带编号、截图路径和时间，也可以按 Ctrl+C；“只复制文字”只要原话，每条空一行。
  - “删除”要再点一次确认。删掉的记录进回收站，提示条上能撤销。
- 点日历图标打开月历，有记录的日子开一朵花，点它跳到那天。

## 配置阿里云

也可以在“识别来源”中选择本地服务，见下方说明。云端和本地的地址、模型分别保存。

默认模型是阿里云百炼的 `qwen-audio-3.1-asr-flash-message`，WebSocket 流式识别，边说边出字，16kHz/16bit/单声道 PCM。价格以[阿里云语音识别模型页](https://help.aliyun.com/zh/model-studio/asr-model)为准。

| 模型 | 用法 |
| --- | --- |
| `qwen-audio-3.1-asr-flash-message` | 边说边出字（推荐） |
| `qwen-audio-3.1-asr-flash`、`qwen-audio-3.0-asr-flash` | 说完后整段转写。单次上限 5 分钟，超过时自动改用推荐模型 |
| `paraformer-realtime-v2`、`fun-asr-realtime` | 上一代实时模型 |
| `qwen-audio-3.1-asr-flash-filetrans` | 不可用：只接受公网文件地址，圈说的录音在本机 |

1. 在阿里云百炼控制台创建 API Key。
2. 打开“圈说 → 设置”，粘贴 API Key；语言默认中文，支持英文术语。
3. 点“测试连接”。测试会短暂打开麦克风，再向阿里云确认 Key 和模型可用。
4. 可选：填写业务空间专属 `wss` 地址、热词表 ID、麦克风设备。

API Key 只使用 Windows DPAPI 的当前用户加密保存，不在 `settings.json`、日志、截图或崩溃报告中保存明文。

**没有 Key 也能用**：正常截图、录音和手打说明。无 Key 的记录标为“转写未完成”，配置后可从保留的 WAV 重新转写。

## 配置本地语音识别

主程序不捆绑模型或推理环境，不按显卡容量锁定模型。打开“设置 → 语音识别”，选择“本地服务”，按两步复制提示词：

1. **① 复制检测提示词**：发给能操作电脑的 AI，让它检查是否适合、会不会影响其他软件，用几句人话告诉你结果。这一步不会安装。
2. **② 复制安装提示词**：想安装时把它发给 AI，它会自主完成常规安装、连接和排错，不要求你整理摘要或逐步确认。模型卡的提示词指定该模型；通用提示词沿用已选模型，没有指定时由 AI 检查后选一个省资源的。涉及系统变动、影响其他应用或明显超出你的资源上限时才询问。
3. 三张模型卡分别提供“① 看电脑适不适合”和“② 帮我装好”：**Paraformer Streaming INT8、Qwen3-ASR 0.6B、Qwen3-ASR 1.7B**。已有模型时让 AI 复用并补齐连接。完成后点击“刷新已接入模型”，再点对应卡片“验证并切换”，核对实际模型身份后自动保存，失败保留原配置。

模型卡显示的是初步资源预算；是否适合取决于当时可分配的显存与内存。可以安装多个模型，服务按需加载，不能把“已登记”当成此刻已运行。正在录音、重转写或测试连接时不能切换。

- [第一阶段：检测选型](docs/local-asr-assessment-prompt.md)和[第二阶段：安装接入及协议](docs/local-asr-agent-prompt.md)是两份独立提示词，与两个按钮的内容分别一致。源码、便携 ZIP 和安装目录的 `docs` 都提供这两份文件，exe 内也包含它们。
- 默认地址为 `ws://127.0.0.1:8765/asr`，模型/配置名称为 `default`；填写 agent 实际配置的值。服务必须实现 `quanshuo-asr-v1`，任意 OpenAI/FunASR 地址不能直接视为兼容。
- “测试连接”发送半秒静音，验证音频收发及完成协议，不打开麦克风。实际模型的准确率、首字延迟、显存占用由安装 agent 用语音测试，并说明未测项。
- 边说边显示临时文字，当前句确认后继续下一句。支持本地重新转写；服务断开、超时或积压时保留已有文字与 WAV，不会自动切到云端。
- 一次录音沿用开始时的识别配置；更改设置在下一次录音生效。模型常驻、释放及电脑重启后的服务启动由安装 agent 配置。

开发验证（合成音频和协议服务，不代表真实模型性能）：

```powershell
dotnet run --project tests/QuanShuo.LocalAsr.Tests
npm --prefix frontend run test:local-asr
npm --prefix frontend run test:local-asr-races
```

真实服务验收见安装提示词中的 `--smoke-local-asr` 命令。`protocolOk` 表示协议完整结束，`ok` 还要求文字非空；二者都不代表实时出字或准确。默认 `--smoke-pacing 100` 按实时节奏发送，`streamingObserved` 检查音频发送期间是否已有非空文字，并报告准备、首字和结束等待时间。`--smoke-pacing 0` 用于快速重转写，此时 `streamingObserved` 为 `null`，不作实时延迟判断。用足够长的真实语音样本核对结果，不能只测端口或静音。

## 文件与隐私

默认目录：`%USERPROFILE%\Documents\QuanShuo`。

```text
2026-10-31/
  143022.png
  143022.md
  143022.wav   （转写失败或选择保留时）
```

同秒创建会追加 `-2`。Markdown 包含时间、来源程序、窗口标题、图片引用和原话，可脱离应用直接打开。修改数据目录后需要重启圈说，旧记录不会自动迁移。

录音时 WAV 头和文字持续更新。断网、Key 错误或服务异常不会删除已有文字或 WAV；转写成功后默认删除 WAV，可改为保留。

## 常见问题

- **快捷键按了没反应**：组合可能被其他程序（QQ、输入法、驱动工具等）占用。打开设置会看到占用提示，换一个组合保存即可，即时生效。
- **启动后没看到窗口**：圈说常驻托盘。再次运行 `QuanShuo.exe` 会唤起历史窗口；也可以双击托盘图标。
- **麦克风被拒绝**：在设置点“打开麦克风权限”，检查 Windows 隐私设置和输入设备。截图不会因此丢失。
- **断网或转写失败**：打开记录点“重新转写”，已有文字和 WAV 会保留。
- **画面不支持 WebGL**：自动退回静态场景，历史、编辑和复制仍可用。安静模式和系统“减少动画”会停止全部氛围动效。
- **手动改动文件**：SQLite 索引会从 Markdown/PNG 重建和同步；索引本身可删除，不是唯一数据源。

## 开发与构建

要求：.NET 10 SDK、Node.js 24、Windows 10/11。便携版与安装版都由同一套 .NET 发布流程生成。

```bash
cd frontend
npm ci
npm run build
cd ..
dotnet build src/QuanShuo.Native/QuanShuo.Native.csproj
```

一键发布自包含 exe、便携 ZIP 和可用的安装版：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

输出在 `artifacts/`。安装器内嵌主程序，所以构建完整安装包请使用上面的一键脚本。发布产物不提交到 git，安装包通过 GitHub Releases 分发。

本地验证入口：

```powershell
# 真实抓帧、矩形/自由圈选、同秒命名、索引和复制格式
QuanShuo.exe --smoke-capture --smoke-output artifacts

# 打开本地麦克风约 0.6 秒，不上传；检查 DPAPI 和无 Key 失败恢复
QuanShuo.exe --smoke-audio --smoke-output artifacts

# 用已保存的 Key 转写一个 16kHz 单声道 WAV，结果和原始错误写入 result.json，不含 Key
QuanShuo.exe --smoke-asr --smoke-input sample.wav --smoke-model qwen-audio-3.1-asr-flash-message --smoke-output result.json

# 走真实录音流程：测试连接、重新转写、暂停 30 秒保活、录完再转写
QuanShuo.exe --smoke-coordinator --smoke-input sample.wav --smoke-long over-5-minutes.wav --smoke-output result.json

# 真窗口走完安装、升级、卸载全流程。圈说运行中或已安装时会拒绝执行
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-installer.ps1
```

前端可完全脱离 .NET 运行和截图：

```bash
cd frontend
npm run dev
npm run shots
```

详细模块、消息、动效、素材、基线和已知问题都在 `docs/frontend/`。

## 许可

圈说代码采用 [MIT](LICENSE)，Copyright © 2026 naname737373naname73-cmyk。字体、依赖与模型的许可见 [第三方声明](THIRD_PARTY_NOTICES.md)。
