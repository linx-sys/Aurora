# Changelog

所有重要变更记录于此。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

## [未发布]（develop 分支，目标 3.0）

### 架构
- MainWindow 909 → 222 行：职责收敛为装配根 + 参数处理 + 生命周期；Toast/进度计时/快捷键/联网匹配/窗口杂项/播放状态 UI/命令接线拆分为独立 Controller
- 新增 Application 层 `PlaybackCoordinator`：播放业务唯一入口（PlayTrack/Next/Prev/预载/跨淡/ReplayGain 调度/会话竞态过滤），MainViewModel 不再含播放流程判断
- `IPlaybackService` / `IAudioOutput` / `ILibraryStore` 接口边界确立，引擎可注入 fake 测试
- PlayerEngine 586 → 468 行：频谱采样（SpectrumCapture）与输入/预载管理（TrackInputManager）拆出，收敛为协调层

### 数据与性能
- SQLite 升级为库数据正式核心存储（ILibraryStore）：启动 DB 秒开列表 + 后台文件系统差分同步
- 物化零探测快路径：10k 首秒开 28ms（优化前外推 ~13s），10 万曲库启动进入 <1s 区间
- 搜索输入 200ms 防抖合并
- 输出设备后台预热，启动不再阻塞 UI 线程

### 功能
- SMTC 系统媒体传输控制：媒体键（播放/暂停/上一首/下一首）+ 锁屏/系统浮层曲目信息
- 任务栏 JumpList 最近播放（≤8 条）
- 音频诊断面板（设置 → 音频诊断…）：解码格式/采样率/位深/声道/ReplayGain/输出设备快照 + 打开日志文件夹
- 自动更新体验：更新日志展示 + 带进度下载 + 一键唤起安装器（便携版保持跳转发布页，不静默替换）
- 联网匹配数据源级启停开关

### 测试与质量
- 单元测试 173 → 259：引擎测试 58 例（fake 输出注入，无声卡可跑，覆盖竞态/跨淡/预载/ReplayGain/自然结束）
- 四工程开启 Nullable，核心层（引擎/协调器/模型/服务）警告清零
- CI 发布产物新增 Aurora-symbols.zip（PDB 调试符号）

## [2.0-stable] - 2026-09-10

五项改进合入前的稳定基线（tag `v2.0-stable`）：播放管线常驻混音器重构、SQLite 缓存增量扫描、安装器/便携版/CI 自动化、9 格式支持、ReplayGain 2.0、联网匹配双源降级。
