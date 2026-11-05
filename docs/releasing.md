# 维护与发布

圈说目前是 Windows 预览版。一次发布同时包含可追溯的源码、安装包、便携 ZIP、校验文件和面向用户的变化说明。不要覆盖旧版附件或移动已经发布的版本标签。

## 普通改动

1. 从最新 `main` 建分支，保持一次改动的范围清楚。
2. 先复现问题，再修复；提交 PR 时写清问题、结果及实际验证，不把“编译通过”当成功能验收。
3. 查看自动检查结果。CI 负责无硬件测试和打包校验；麦克风、输入法、真实按键和混合 DPI 仍需本机验收，并记录未测项。
4. 自审或请另一位开发者检查后合并。小型项目不强制多人审批，但不要跳过失败的检查。

构建和测试命令见[贡献说明](../CONTRIBUTING.md)。工作流固定官方 Action 的提交，更新时应重新核对来源和兼容性。

## 发布一个版本

1. 更新 `Directory.Build.props` 版本、`CHANGELOG.md` 和使用说明。旧 PDF/图片仍应标明原适用版本；不要只改文件名装作新版。
2. 在 Windows 完成需要的手动验收。空格相关修改运行 `tests/QuanShuo.Recording.Tests` 的普通检查和 `--global-space`，单独运行，不操作其他窗口。识别服务验证另按协议说明进行。
3. 提交所有计划发布的更改，确认工作区干净，再执行 `tools/build.ps1`。程序的产品版本包含该源码提交，安装包与 ZIP 都来自同一次构建。
4. 运行 `dotnet run --project tests/QuanShuo.Core.Tests -c Release -- verify-delivery`，校对程序、内嵌前端、提示词、许可、随包 README 和下载文件的校验值。
5. 推送源码并确认 GitHub 检查通过，为同一提交创建 `v版本号` 标签和 Release 草稿。上传下列三个文件，核对文件名、大小及 SHA-256 后再发布。当前版本标记为 **Pre-release（预发布）**。

| 本地文件 | GitHub 附件名 |
| --- | --- |
| `artifacts/installer/QuanShuo-Setup.exe` | `QuanShuo-Setup.exe` |
| `artifacts/package/QuanShuo-portable.zip` | `QuanShuo-portable.zip` |
| `artifacts/package/SHA256SUMS.txt` | `SHA256SUMS.txt` |

Release 说明先写用户能感受到的变化、适用系统、如何升级和已知限制，再写验证情况。明确：先从托盘退出再覆盖安装；记录和设置保留；本地模型不在安装包中。

发布后从公开页面确认附件可下载、文件校验值正确、源码标签指向预期提交。更新首页明确版本的下载链接；预发布版本不要使用 `/releases/latest` 作为唯一入口。自动检查成功不等于已发布，Release 草稿也不算公开交付。

## 仓库首页

简介建议：

> 圈说｜Windows 截图与语音转文字工具：圈住屏幕、说出想法，一键把截图和文字交给 AI；支持本地或云端识别，记录保存在本机。

Website 可指向[使用指南](https://github.com/naname737373naname73-cmyk/QuanShuo/blob/main/docs/guide/README.md)。主题标签可用 `windows`、`screenshot`、`speech-to-text`、`asr`、`voice-input`、`desktop-app`、`csharp`、`dotnet`、`local-first`。

Issue 表单和 PR 模板位于 `.github/`。安全反馈入口见 [SECURITY.md](../SECURITY.md)；启用私密漏洞报告后，确认普通访客确实可以找到入口，再更新文档，避免承诺不存在的通道。

## 发布前的隐私核对

只提交源码、公开素材和必要测试样本。不要提交 API Key、`.env`、本机设置、浏览历史、真实录音、私人截图或构建缓存。合成数据与上游测试音频需保留来源和许可。发日志和截图前人工检查内容，文本扫描不能证明图片或二进制里没有私人信息。
