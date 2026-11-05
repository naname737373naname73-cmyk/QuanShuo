# 圈说 {{VERSION}} · Windows 预览版

圈住屏幕，说出想法，把截图、原话和来源一次复制给 AI。记录保存在本机，支持本地或阿里云语音识别。

[完整使用说明](https://github.com/naname737373naname73-cmyk/QuanShuo/blob/main/docs/guide/README.md) · [更新与下载](https://github.com/naname737373naname73-cmyk/QuanShuo/releases) · [反馈问题](https://github.com/naname737373naname73-cmyk/QuanShuo/issues/new/choose)

## 开始使用

Windows 10/11 x64 可用，不需要另装 .NET。便携版解压后运行 `QuanShuo.exe`；安装版运行 `QuanShuo-Setup.exe`。圈说启动后常驻右下角托盘，双击图标打开历史记录。

- `Ctrl+Alt+G`：圈选截图。圈好后按空格开始说话，或按 Enter 只保存截图。
- `Ctrl+Alt+H`：只说话。检查文字后可复制，或按 Enter 结束并尝试贴回目标窗口。
- 录音窗口显示时，空格暂停／继续，切到其他应用也有效。**录音或暂停期间，其他应用的普通空格会用于控制圈说。** 在圈说文字框编辑时空格正常输入；结束录音后释放。
- 一分钟没有检测到语音会自动暂停，再按空格或点“继续”恢复。

没有 API Key 也可以截图、录音和手动输入。自动转文字需要在设置中选择云端服务，或让能操作电脑的 AI Agent 安装本地识别服务。主程序不附带模型和推理环境。

本地模型安装分两步：先用[检测提示词](docs/local-asr-assessment-prompt.md)确认电脑是否适合；决定安装后，再发[安装提示词](docs/local-asr-agent-prompt.md)。也可在设置里复制这两份提示词，并选择、验证已接入的模型。

## 更新、数据和依赖

覆盖安装前从托盘右键退出圈说。记录默认在 `%USERPROFILE%\Documents\QuanShuo`，卸载会保留记录和设置。历史与设置界面需要 [Microsoft WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

“复制给 AI”包含图片的本地路径；网页版聊天通常需要另外上传图片，桌面 Agent 才能按权限读取本机文件。云端模式会发送音频到阿里云；本地模式连接本机服务。

当前预览版没有代码签名证书。请从项目 Releases 下载，并用 PowerShell 的 `Get-FileHash` 核对同页 `SHA256SUMS.txt`；不要关闭系统防护。

圈说代码采用 [MIT](LICENSE)。依赖、字体和运行时的声明见[第三方声明](THIRD_PARTY_NOTICES.md)。
