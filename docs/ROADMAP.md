# Aurora 下一阶段开发路线图（v2.x → 3.0）

> 2026-09-10 依据代码现状（commit `b55a89f`，五项改进已合入）逐阶段核对修订。
> 图例：✅ 已达成 ｜ 🟡 部分达成（列差距）｜ ⬜ 未开始
>
> **与原稿的关键差异**：原稿假设"SQLite 仅作缓存、引擎零测试、MainWindow 900 行"，
> 其中相当一部分已在 2026-09-09 的 P0/P1/P2 改进中解决。本文档是据实修订后的剩余工作清单。

## 基线快照（2026-09-10 实测）

| 指标 | 现状 | 3.0 目标 |
| --- | --- | --- |
| MainWindow | 509 行 / 24.9KB | < 300 行 |
| PlayerEngine | 586 行 / 22.4KB | < 500 行 |
| MainViewModel | 553 行 | 仅 UI 状态 |
| 测试 | 191（引擎 12） | 300+（引擎 50+） |
| 启动 | DB 秒开已实现，未量化 | < 500ms（待基线） |
| Nullable | disable（30 处 Dbg 调用点） | enable 无警告 |
| 发布 | CI 打 tag 自动 Build+Test+Release | + CHANGELOG / symbols |

---

## 阶段 0：代码冻结与基线建立 🟡（进行中，develop 分支）

- ✅ 打 tag `v2.0-stable`（= b55a89f）并推送
- ✅ 建 `develop` 分支并推送（阶段产物在此分支提交）
- 🟡 架构文档：ARCHITECTURE.md 已有；✅ 新增 AUDIO_PIPELINE.md / DATABASE.md / DEVELOPMENT.md / PERF_BASELINE.md / ROADMAP.md
- 🟡 性能基线：数据层已自动化（PerformanceBaselineTests，Release 实测：秒开物化 1000 首 1.41s、差分同步 1000 首 1.14s、GetByPrefix 10k 39ms）；**发现热点**：逐文件探测导致超大库秒开不达标（见 PERF_BASELINE.md 优化方案）；GUI 启动/内存/CPU 占位待手动测量

**完成标准**：当前版本可随时回滚；所有后续修改有量化参照。

## 阶段 1：开启 Nullable Reference Types ✅（2026-09-10 达成，分两步落地）

- ✅ 四个 csproj（AuroraPlayer / tests / installer / unins）`<Nullable>enable</Nullable>`——新代码默认 Null 安全
- ✅ **核心层真零警告**（引擎/协调器/模型/服务共 26 文件）：PlayerEngine / TrackInputManager / SpectrumCapture / PlaybackCoordinator / PlaybackController / PlaylistManager / Model（Track 字段语义化 `?`）/ LibraryDatabase + ILibraryStore（TryGet → `TrackRow?`）/ TagReaderService / NetMatch / UpdateChecker / CoverArt / Loudness / Logger / RelayCommand / ViewModelBase / App 等
- 🟡 **UI 层 21 文件过渡性 `#nullable disable`**（文件头标注"批次 2 待清理"）：MainWindow / 各 View Controller / MainViewModel / SmTcController（WinRT）/ Assoc（注册表）/ Installer / Uninstaller / Tags / Id3 / Lrc——控件注入与 WinRT/注册表互操作 null 语义密集，收益低风险高
- 修复要点：事件声明 `EventHandler?` + 处理器 `object? sender`（接口同步）；`TrackRow?/Track?/string?` 按真实语义标注；仅 4 处 `!` 抑制（FingerprintMatches 前置判空后语义、Settings._kv、Array.Copy、FromMetadata row）
- 完成标准达成：主工程与测试工程 **Nullable 警告 0**；259/259 测试全绿

## 阶段 2：MainWindow 二次精简 ✅（2026-09-10 达成：509 → 222 行，<300 目标达成）

已完成（累计 909 → 222 行）：
- ✅ 第一批（P0-1）：Toast / 33ms 计时器（PlaybackTickController）/ 快捷键（HotkeyController）/ 联网匹配（NetMatchViewController）/ 窗口杂项（WindowMiscController）/ UiUtil 静态助手
- ✅ 第二批（阶段 2 收尾）：播放状态 UI 与切歌编排 → `PlaybackStateViewController`（SetPlaying/UpdateTrackInfo/ApplyVolume/音量弹层/静音图标/模式图标/OnCurrentTrackChanged）；按钮接线/音量条拖动/全屏/列表面板/窗口拖动/拖放导入/快捷键挂接 → `CommandBindingManager`（纯薄委托）
- ✅ MainWindow 只剩：Initialize（装配根）/ Constructor 参数处理 / Lifecycle（Closing 保存、Loaded 启动服务）
- 命名维持扁平（不建 src/UI/ 子目录，与项目惯例一致）

**完成标准达成**：MainWindow 无播放逻辑 / 数据查询 / 文件扫描 / 业务判断。

## 阶段 3：MainViewModel 解耦（PlaybackCoordinator）✅（2026-09-10 达成）

已完成：
- ✅ `IPlaybackService` 接口隔离引擎（VM 不引用 PlayerEngine 具体类型）
- ✅ 新增 `PlaybackCoordinator`（Application 层）：PlayTrack / Next / Prev / DeleteTrack / SeekTo / PreloadNextIfNearEnd / CrossfadeToTrack / ReplayGain 懒分析调度 / 引擎会话竞态第二道防线（约 280 行）全部迁出
- ✅ MainViewModel 收敛为：可观察 UI 状态 + 命令转发 + CurrentTrackChanged 事件源（553 → 约 250 行）；音量/静音为纯 UI 状态透传（无流程逻辑）
- ✅ UI 调度经 `Action<Action>` 注入（VM 传 Dispatcher.BeginInvoke，测试内联）——协调器无 UI 依赖、可单元测试（FakePlaybackService，17 例）
- ✅ 单一数据源不破：242→259 测试全绿

**完成标准达成**：VM 不含播放流程判断 / 自动下一首 / Crossfade 决策。

## 阶段 4：PlayerEngine 再拆分 ✅（2026-09-10 达成：586 → 468 行，<500 达成）

已完成：
- ✅ 第一批（P1-4）：`IAudioOutput` + `WaveOutAudioOutput` / `WasapiAudioOutput`（即原稿 OutputManager）
- ✅ 第二批（阶段 4）：`SpectrumCapture.cs`（SampleCaptureProvider 频谱采样，54 行）+ `TrackInputManager.cs`（TrackInput / 输入集合管理 / BuildInput / Preload / 淡出移除计时器，201 行，共享引擎锁）自引擎迁出
- ✅ PlayerEngine 收敛为协调层：状态机、会话 ID、输出设备生命周期、ReplayGain 应用、诊断快照
- PlaybackPipeline 未单独成文件（Decoder→Normalize→GainFade 装配已内聚于 TrackInputManager.BuildInput，约 15 行，拆出收益为负——按极简原则保留）

**完成标准达成**：PlayerEngine 468 行 < 500。

## 阶段 5：Library 数据模型升级 ✅（核心）/ 🟡（扩展）

**原稿主要诉求已达成**（P1-3，2026-09-09）：
- ✅ SQLite 为正式核心存储（ILibraryStore，WAL）
- ✅ 启动流程"SQLite 立即显示 → 后台扫描差分 → 更新库"（LibraryImportController 两段式）
- ✅ 增量扫描（LibraryScanner 语义 = Library.BuildTracksIncremental：指纹命中跳过/变化重解析/消失清理）
- ✅ **超大库秒开达标（2026-09-10 优化）**：物化改零探测快路径，10k 首 28ms（PERF_BASELINE 实测），外推 100k 首 <1s
- ⬜ 剩余：合成大库端到端验证（构造 10 万行 DB + GUI 实测）；快路径代价已文档化（幽灵行/歌词封面由同步补齐）

剩余（按需推进，非必需）：
- ⬜ 扩展列：Genre / Bitrate / SampleRate（TagReaderService 已有部分数据，加列+迁移即可）
- ⬜ 新表：artists / albums / playlists / playlist_tracks / history / favorites —— **属产品功能**，建议待播放队列/收藏功能立项时一并设计，勿提前建空表
- ⬜ Hash 列：当前指纹 = 大小+mtime，够用；content hash 成本高，无现实问题不建议加
- ⬜ 10 万曲库启动 <2s：需先有阶段 0 基线与合成大库再验证（BuildTracksFromRows 已按 O(n) 物化，预估可达成）

## 阶段 6：Audio Engine 自动化测试 ✅（2026-09-10 达成：引擎 58 例，全量 242）

已完成（P1-4 基线 12 例 + 阶段 6 补强 46 例，fake IAudioOutput 无声卡可跑）：
- ✅ 播放/暂停状态机、完整状态事件序列（Load→Play→Pause→Play→Stop）
- ✅ Seek 钳制（负值/超时长/精确到末尾→自然结束）、解码失败、音量钳制与设备同步
- ✅ 自然结束：仅一次、携会话 ID 与路径、状态事件收尾 Stopped、结束后可重载再播
- ✅ 快速切歌竞态：连切 A→B→C 会话单调；播放中硬切只发最新会话结束事件（A 静默移除）
- ✅ 跨淡：播放中跨淡不 Pause、零淡入、无前置 Load、长淡入仍自然结束、旧输入淡出后静默移除（计时器路径）
- ✅ Preload：命中复用（解码只开一次）/同路径幂等/路径变更丢弃旧预载/不存在路径/解码抛异常不崩溃
- ✅ ReplayGain 联动：非静音流+峰值捕获——默认 1×、实时生效 2×、Load 前设置生效、±钳制（8×/0×）
- ✅ Stop/Dispose 幂等与 Dispose 后全调用 no-op；输出工厂抛异常/返回 null 降级（Load 可用、Play 静默、Volume 记忆）
- ⬜ 可选后续：WASAPI 真机冒烟（本地手动）、并发 Load/Ended 压力循环

**完成标准达成**：引擎测试 58 ≥ 50；全量 242/242，连续 6 轮无 flake。

## 阶段 7：Audio Diagnostics 面板 ✅（2026-09-10 达成）

- ✅ `PlayerEngine.GetDiagnostics()`：当前曲目/解码格式/采样率/位深/声道/时长/混音器输入数/ReplayGain（dB）/跨淡设置/输出设备（含独占请求与回退结果）/输出就绪状态/会话 ID
- ✅ `AudioDiagnosticsDialog`：只读等宽文本展示 + 复制 + **打开日志文件夹**（阶段 8 导出入口）
- ✅ 入口：设置对话框（NetMatchSettingsDialog）"音频诊断…"按钮（audioDiagnostics 提供器可选注入）

## 阶段 8：日志系统统一 🟡（2026-09-10 务实达成导出能力）

现状：Logger 已有轮转 + 三路崩溃钩子 + `LogDir` 常量；诊断面板提供"打开日志文件夹"。
- ✅ 完成标准"用户可导出日志"：诊断面板 → 打开日志文件夹（`%LOCALAPPDATA%\Aurora\Logs\`）
- ⬜ 可选后续：ILogger 接口抽象 + 30 处 Dbg 调用点分级迁移——收益主要是按级别过滤，
  当前单文件轮转日志已够定位问题，**按极简原则暂不实施**（记录为决策而非欠账）

## 阶段 9：Provider 系统统一 ✅（2026-09-10 评审 + 增量达成）

评审结论：现有架构即原稿诉求——`ILyricsProvider`（可插拔接口）+ `LyricsProviderBase`（HTTP/JSON/打分/缓存共享设施）+ 注册表 `NetMatch.Providers`（按序尝试/单源失效自动降级）+ 按格式启停；**"替换任意 Provider 无需修改核心代码"完成标准天然达成**（实现接口并加入 Providers 即生效）。
- ✅ 增量（2026-09-10）：**数据源级启停**（设置键 `netmatch.provider.<Name>`，设置对话框动态生成开关，禁用源编排直接跳过）
- ⬜ MetadataProvider：无消费场景（标签本地读取），按极简原则不建空抽象；未来需要时实现 `ILyricsProvider` 变体即可

## 阶段 10：产品体验优化 🟡（核心已达成，剩截图）

- ✅ 启动：DB 秒开 + 设备后台预热（量化见 PERF_BASELINE）
- ✅ 搜索：200ms 防抖；切歌：预载/跨淡无缝衔接
- ✅ 首次启动引导：文件夹选择对话框（已有）；设置页整理：诊断入口/数据源启停/更新开关已归类
- ✅ 快捷键说明（README 表格）
- ⬜ README 软件截图：**需配合**（运行应用截图 1~3 张替换 README 顶部 TODO）

## 阶段 11：发布体系升级 ✅（2026-09-10 达成）

- ✅ CI：tag → Build → Test → Release 自动化（既有）
- ✅ 新增 `Aurora-symbols.zip`（PDB 调试符号包，崩溃堆栈翻译用）
- ✅ `CHANGELOG.md` 建立（2.0-stable 基线 + 3.0 未发布变更按 Keep a Changelog 格式记录）
- 产物命名维持 AuroraPlayer-Setup.exe（改名收益低，README 引用需同步改动，不做）

---

## 关于"最终目标架构"（多项目拆分）的决策建议

原稿提出拆 Aurora.App / Core / Audio / Library / Infrastructure / Application 六工程。**与本项目一贯的"src/ 扁平单工程极简"风格冲突**（40+ 源文件全在一个 csproj，csproj 手工管理编译项）。替代方案：

- **保持单工程**，用命名空间分区（Aurora.Audio / Aurora.Library / Aurora.Application）+ 文件头职责注释表达分层，依赖方向靠阶段 3/4 的接口边界保证
- 多工程拆分仅在"开始接第三方扩展/插件"时再考虑

**此项需拍板，默认按单工程推进。**

## 推荐实施顺序（修订后）

1. 阶段 0：tag + develop 分支 + 性能基线（半天）
2. 阶段 6 补强：快速切歌竞态等引擎测试（~30 例）
3. 阶段 3：PlaybackCoordinator（P0 核心）
4. 阶段 2 收尾：MainWindow < 300 行
5. 阶段 1：Nullable enable（单独一批）
6. 阶段 4：PlayerEngine 拆分收尾
7. 阶段 7 + 8：诊断面板 + 日志统一（一批）
8. 阶段 9 / 10 / 11 按需
