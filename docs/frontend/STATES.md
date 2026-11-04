# 界面状态

所有状态都能用 URL 打开。基线截图由 `npm run shots` 生成，放在 `screenshots/`，命名为 `状态-尺寸-时段.jpg`。

| 状态 | URL | 看什么 |
| --- | --- | --- |
| 正常 | `?state=normal&records=23` | 整头鲸亮起，主峰露出 |
| 详情 | `?state=detail` | 截图悬浮、倒影、检视器 |
| 多选 | `?state=selected` | 三张堆叠，右侧列出文字 |
| 悬停 | `?state=hover` | 星和列表项同时高亮 |
| 录音中 | `?state=recording&wind=0.8` | “正在听”面板、列表标记、水面涟漪 |
| 转写失败 | `?state=failed` | 提示条和“转写未完成”标记 |
| 空记录 | `?state=empty` | 空状态线稿和快捷键，星座只有虚线 |
| 加载中 | `?state=loading` | 骨架屏 |
| 搜索无结果 | `?state=search-empty` | 提示换词 |
| 设置 | `?state=settings` | 右侧长卷 |
| 首次运行 | `?state=onboarding` | 面板隐藏，字直接放在星空里 |
| 安静模式 | `?quiet=1` | 所有动效停止 |
| 回退 | `?webgl=0` | 静态图加星座 |

固定画面用 `&day=3&t=12.5&seed=1`。

基线覆盖范围：`normal`、`detail` 三种尺寸四个时段；其他状态 1600×1000，两个时段。比旧方案少，因为新场景细节极多，全量 PNG 会让仓库增加几百 MB。

## 浅色和深色

`day` 四舍五入到 2、3 时用深色令牌，0、1 时用浅色令牌。配色切换发生在滑块中点。
