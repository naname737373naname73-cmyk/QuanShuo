# 参与圈说开发

欢迎反馈问题、改进说明文档，或提交代码。圈说仍是 Windows 预览版；小而清楚的改动更容易验证和合并。

## 反馈与讨论

- 使用问题或功能建议请到 [Issues](https://github.com/naname737373naname73-cmyk/QuanShuo/issues)，尽量写清版本、操作步骤和实际结果。不懂技术术语也没关系。
- 涉及凭据泄露、越权或其他安全问题，先看 [安全反馈说明](SECURITY.md)，不要在公开 Issue 中上传敏感细节。
- 较大的功能或界面调整，先开 Issue 说明要解决的问题；修正文案、链接或明确的小问题可以直接提 PR。

## 准备开发环境

需要 Windows 10/11 x64、.NET 10 SDK、Node.js 24 和 Git。SDK 规则见 [global.json](global.json)，前端依赖使用已提交的锁文件。

先 Fork 仓库，再克隆自己的 Fork，从 `main` 新建工作分支。以下命令都在仓库根目录执行：

```powershell
npm.cmd --prefix frontend ci
npm.cmd --prefix frontend run build
dotnet build src/QuanShuo.Native/QuanShuo.Native.csproj -c Release
```

只开发网页界面时，可运行 `npm.cmd --prefix frontend run dev`，使用内置模拟数据，不必配置麦克风或语音模型。

完整打包使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1
```

输出在 `artifacts/`。安装器需要内嵌主程序，请使用完整打包脚本；不要把安装包、模型、运行环境或 `node_modules` 提交到 Git。

## 按改动选择检查

先跑与改动相关的检查，在 PR 中写明命令和结果；没有执行的检查直接注明。

| 改动 | 检查入口 |
| --- | --- |
| 文件保存、记录读写 | `dotnet run --project tests/QuanShuo.Core.Tests -c Release` |
| 本地语音协议、模型切换、收尾保存 | `dotnet run --project tests/QuanShuo.LocalAsr.Tests -c Release` |
| 前端类型与打包 | `npm.cmd --prefix frontend run build` |
| 前端语音设置 | `npm.cmd --prefix frontend run test:local-asr`、`test:local-asr-races` |
| 前端草稿、批量操作、皮肤 | `npm.cmd --prefix frontend run test:drafts`、`test:manage`、`test:skins` |
| 窗口状态、录音快捷键 | 见 [录音窗口测试说明](tests/QuanShuo.Recording.Tests/README.md) |
| 安装包与源码对应关系 | 完整打包后运行 `dotnet run --project tests/QuanShuo.Core.Tests -c Release -- verify-delivery` |

前端浏览器检查需要可用的浏览器。脚本会尝试已有的 Edge/Chrome；若没有，在 `frontend` 目录执行 `npx.cmd playwright install chromium` 安装测试浏览器。更多场景及测试边界见 [tests/README.md](tests/README.md)。

`QuanShuo.Recording.Tests` 会打开测试窗口并取得焦点；`--global-space` 还会发送真实按键。只在本机交互桌面中串行运行，运行期间不要操作其他窗口。**不要把这些焦点测试、真实截图、麦克风、真实模型或云端 Key 测试放进无人值守 CI。** 协议测试使用合成数据，不代表真实模型准确率或硬件兼容性。

## 提交 PR

- 一次 PR 解决一个明确问题，说明用户操作前后的变化；修改界面时可附不含私人内容的截图。
- 修复可复现缺陷时，保留或补充能捕获该缺陷的回归检查，不要靠删除断言让测试通过。
- 修改行为、快捷键、配置或发布内容时，同步相关说明；新增依赖或素材时补充来源与许可。
- 不提交 API Key、私人截图、录音、聊天内容、用户配置或个人机器上的部署目录。示例和测试使用合成数据。

贡献按项目的 [MIT 许可证](LICENSE) 提供。第三方代码、素材和模型仍需遵守各自的许可。
