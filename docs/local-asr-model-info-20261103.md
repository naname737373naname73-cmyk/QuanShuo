# 本地模型资料与选型参考（2026-11-03）

设置原来只有服务地址和配置 ID，默认值 `default` 不能告诉使用者实际加载了什么。本次把语音识别移到设置首组，增加实际模型资料、设备、精度和资源预算，并列出三个选型候选。展示不触发下载或模型加载，推荐项不充当模型切换器。

## 数据如何到达界面

`quanshuo-asr-v1` 的 `ready` 可带 `modelInfo`：实际 `name`、`runtime`、`device`、`precision`、可选 `estimatedVramMb`、`estimatedRamMb`。内存单位 MB，1024 MB = 1 GB。名称缺失/default、资料类型错误、负值或过大预算不会被用作有效资料；可选字段问题不影响旧识别协议。

静音连接测试返回资料，并独立原子保存到 `%LOCALAPPDATA%\QuanShuo\local-asr-model.json`。缓存包含被测试的地址、配置 ID、结果、时间；不写回设置。重新打开设置只读此缓存，地址或配置 ID 不匹配就隐藏旧资料。失败测试清除本次模型资料。卡片标注“上次测试”，不声称后台正在运行。旧服务可以正常识别，未报告信息时明确展示未知。

诊断命令也输出 `modelInfo`，仍不改设置、不写缓存、不开麦克风。安装提示词要求新适配器回传真实模型信息，并核验界面；第一阶段仍只读。

## 推荐的依据与预算假设

这是选型起点，按日常可分配资源选择，并非按显卡总容量固定分档。数字是项目给出的初步预留估算，没有通用的“最低显存”保证。CPU 占用的是系统内存；GPU 预算不是权重文件大小。

| 候选 | 初步预留 | 使用前提 |
|---|---|---|
| Paraformer Streaming INT8 / sherpa-onnx CPU | 模型显存 0 GB，系统内存约 1–2 GB | Windows 原生、单路流式；仍需验证 CPU 能跟上说话 |
| Qwen3-ASR 0.6B | 模型显存约 4–6 GB | 单路 BF16、无强制对齐模型、有界缓存；额外留系统内存和其他软件资源 |
| Qwen3-ASR 1.7B | 模型显存约 6–8 GB | 同上；为准确率对照试验提供候选，不保证更适合每位用户 |

GPU 数字由参数规模、16 位权重及运行时/缓存开销作工程预留估算，**不是官方发布的实测配置**。vLLM 的显存预分配、上下文和缓存参数会改变实际占用，未限制缓存时可能超过此范围。预算需由安装 agent 按所选运行时重算并实测；量化和其他后端另行评估。6GB 卡若常用软件已占很多，CPU 候选可能更合适；24GB 卡也不必选择更大模型。

截至本次查阅，Qwen 官方 streaming API 使用 vLLM；vLLM GPU 路径原生支持 Linux，Windows 要评估 WSL2 和实际 GPU/驱动兼容性。不能把 Transformers 文件转写输出流冒充连续音频流式识别。

官方来源（2026-11-03 查阅）：

- [sherpa-onnx 在线 Paraformer：INT8、Windows、实时麦克风示例](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-paraformer/paraformer-models.html)
- [Qwen3-ASR：0.6B / 1.7B 与 streaming 后端](https://github.com/QwenLM/Qwen3-ASR)
- [vLLM GPU 安装与系统支持](https://docs.vllm.ai/en/latest/getting_started/installation/gpu/)

界面提供相应官方资料入口。后续模型或引擎升级时应同时更新参考日期、预算假设与两阶段提示词，不把内置推荐列表当作永远有效的配置。

## 本次验证和交付

- 39 项本地识别回归通过，新增实际模型资料传递、旧服务兼容、错误资料降级、无效预算拒绝和独立缓存检查。
- 10 项设置异步检查通过，包括打开设置不查询/加载模型、迟到的旧模型信息不能污染新配置、未知显存不显示成 0、服务文字按纯文本渲染、失败后清除当前资料。
- 星湖/海棠两套真实前端检查通过：独立连接设置、两阶段完整提示词、CPU 显存 0、重新打开保留资料、地址/模型变更隐藏旧资料。截图在 `artifacts/local-asr-ui/*-model-info.png` 和 `*-model-choices.png`。
- 完整构建和交付字节校验通过，27 个前端文件、程序集、安装器/ZIP 的 EXE、两份内嵌/随包提示词一致。
- 已更新 `F:\QuanShuo`；安装时设置文件与桌面快捷方式哈希不变，旧程序备份在 `artifacts/install-backups/20261103-205215-model-info`。
- 现有 `F:\QuanShuo-LocalASR\server.py` 只增加 ready 的资料字段，原文件备份为 `artifacts/local-model-info-20261103/server-before.py`。确认没有活动连接且圈说已退出后重启其自己的服务；未更换模型或依赖，未新增下载。
- 新 EXE 对真实本地服务发送合成静音：`protocolOk=true`，返回 `paraformer-bilingual-zh-en-int8 / sherpa-onnx 2.0.1 / cpu / INT8 / estimatedVramMb=0 / estimatedRamMb=1024`。静音文本为空，因此诊断的 `ok=false`、退出码 1 是预期，不能拿来声称真实语音准确率通过。
- 已启动安装后的圈说，在设置中完成静音连接测试，实际模型、设备、显存 0 GB、预计系统内存约 1 GB 正确显示；资料已由原生桥写入独立缓存。

本次 EXE SHA-256：`04580839DB462E654314DD998CC9AB0583D83D64DFEB5B9EDB4DEA06ECB44F17`。

证据目录：`artifacts/local-model-info-20261103`，含 `protocol-tests.log`、`ui-tests.log`、`ui-races.log`、`release.log`、`delivery.log`、`live-model-info.json`、`installed.json`、`installed-model-cache.json`。未重测人声输入、准确率、长时显存峰值或 Qwen 候选；此前使用者安装的环境与模型继续保留。
