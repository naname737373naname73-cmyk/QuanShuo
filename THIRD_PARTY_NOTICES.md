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

## WebRtcVad.NET 1.0.0

本机静音检测使用 [WebRtcVad.NET](https://github.com/devnull/WebRtcVad.NET)，固定为 NuGet 1.0.0（源码提交 a52dac432a64c2c609addf978c1f766bde6a5264）。这是无原生依赖的 WebRTC VAD C# 移植，不包含 ASR 模型。包作者署名 Mike；上游提供的完整许可如下：

The MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

================================================================================

WEBRTC VAD ALGORITHM LICENSE

The voice activity detection algorithm in this library is a C# port of code
from the WebRTC project (https://webrtc.org/). The original C implementation
is subject to the following license:

Copyright (c) 2011, The WebRTC project authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
