# 第三方组件与素材

圈说自身代码采用根目录的 MIT 许可证；第三方组件、字体和模型保持各自的许可。

## 字体

`frontend/public/assets/fonts/noto-serif-sc-subset.woff2` 是 Noto Serif SC 的界面字体子集。原字体元数据中的版权声明为 © 2017–2023 Adobe (http://www.adobe.com/)。字体采用 SIL Open Font License 1.1，不随圈说代码改为 MIT。

完整许可随附在 [OFL.txt](frontend/public/assets/fonts/OFL.txt)，来源为 [Noto CJK Serif 官方仓库](https://github.com/notofonts/noto-cjk/tree/main/Serif)。子集工具见 `frontend/tools/subset-font.mjs`。系统字体由用户的 Windows 提供，不在仓库中分发。

## 依赖

NuGet 直接依赖为 Microsoft.Data.Sqlite、Microsoft.Web.WebView2、NAudio；前端开发依赖为 TypeScript、Vite、Playwright、subset-font 和 Node.js 类型定义。具体版本见 `.csproj` 与 `frontend/package-lock.json`，各包的许可和版权声明以包内元数据为准。

发布程序包含 .NET 运行时及相关运行库，其许可不会被本项目的 MIT 许可证替代。自行再分发程序或依赖时，需要同时保留适用的第三方声明。

## 图像与模型

星湖场景、图标由项目代码生成或绘制；海棠插画由 GPT 图片模型生成，来源和加工方式见 [素材说明](docs/frontend/ASSETS.md)。外部风格参考图不在仓库内。

本项目不包含 ASR 模型权重或第三方推理服务。使用者按需安装模型，并遵守对应模型与运行环境的许可；MIT 不授予这些模型的额外权利。
