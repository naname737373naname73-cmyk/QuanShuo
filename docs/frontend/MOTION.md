# 动效

确认和取消永远不等动画。安静模式和系统“减少动画”下，下表全部关闭。时长写在 `tokens.css` 的 `--motion-*`，场景参数在 `scene.config.json`。

| 动效 | 触发 | 时长与曲线 | 可打断 | 实现 |
| --- | --- | --- | --- | --- |
| 截图升起 | 选中记录 | 460ms，`--ease-out`，上移 26px 并淡入 | 是 | `rise`，`history.css` |
| 倒影出现 | 截图解码完成 | 约 0.4 秒线性 | 是 | `SceneRenderer.draw` |
| 入水涟漪 | 选中记录 | 约 7 秒衰减 | 不阻塞 | `SceneRenderer.setStageImage` |
| 检视器滑入 | 选中记录 | 460ms，右移 16px | 是 | `slide-in` |
| 新星闪耀 | `records.created` | 1.1 秒，指数衰减 | 不阻塞 | `ConstellationLayer.drawStar` |
| 流星 | 复制给 AI | 1.3 秒 | 不阻塞 | `lake.frag.glsl` 的 `uMeteor` |
| 说话涟漪 | 电平高于阈值 | 每 520ms 投一圈 | 停止说话即停 | `spawnVoiceRipple` |
| 水面细浪 | 电平 | 起音 60ms，释音半衰期 420ms | 跟随电平 | `uVoice` |
| 指针涟漪 | 鼠标划过水面 | 每 260ms，强度 0.22 | 不阻塞 | `onPointerMove` |
| 点击涟漪 | 点击水面 | 强度 1 | 不阻塞 | `onClick` |
| 时段过渡 | 跟随系统或切换设置 | 指数趋近，约 1 秒 | 是 | `draw` |
| 时段拖动 | 拖标题栏滑块 | 立即 | 是 | `App.bindTitlebar` |
| 配色切换 | 时段跨过中点 | 900ms 颜色过渡 | 是 | `.glass` 的 transition |
| 视差 | 鼠标横向移动 | 缓动，幅度很小 | 是 | `uParallax` |
| 列表悬停 | 指针 | 140ms | 是 | CSS |
| 提示条 | 操作结果 | 240ms 进出，3.2 秒消失，撤销条 8 秒 | 是 | `App.toast` |

序列帧在 `docs/frontend/motion/`，`voice-00` 到 `voice-05` 是说话时的涟漪。

原生层动效见 `HANDOFF.md` 第 3 节：圈选的星尘拖尾和四角闪烁、录音卡片的声纹涟漪。
