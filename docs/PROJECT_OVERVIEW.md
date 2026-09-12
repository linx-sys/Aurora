# Aurora 项目概览（记忆与上下文整合版）

> **本文档性质**：对 WorkBuddy 中关于本项目的**全部记忆、日志、评估与验证记录**做去重归类后的单一权威概览。
> **整合来源**：`.workbuddy/memory/2026-09-08 ~ 09-10.md`、`.workbuddy-ai/memory/2026-09-11.md`、`.workbuddy/tmp_chatgpt_review.txt`（外部评审）、`outputs/Aurora-本地音乐播放器项目评估报告.md`（静态评估）、`outputs/Aurora-validation-2026-09-11.txt`（修复验证）、`docs/*.md`、`README.md`、`CHANGELOG.md`、git 历史与工作区实际代码。
> **更新时间**：2026-09-13 00:50　**代码基线**：`main` = `develop` @ `4874694`（v3.0.1 发布提交）｜最近发布：**`v3.0.1`**（2026-09-13）。
> **口径说明**：文中「已验证」表示有构建/测试/真机检查证据；「已落地未复验」表示代码已改但仅经静态确认；「未验证」表示仅有静态推断。
> **测试数字**：当前实测 **389**（`dotnet test tests/Aurora.Tests.csproj`）；历史条目中的数字为该时点快照，不作当前值使用（口径规则见 CHANGELOG）。

---

## 1. 项目身份卡

| 项 | 内容 |
| --- | --- |
| 名称 | Aurora · 极光音乐（产物名 `AuroraPlayer-Setup.exe`） |
| 形态 | Windows 桌面**本地**音乐播放器（非流媒体、无账号、无广告、无遥测） |
| 技术栈 | .NET 10 + WPF（MVVM + View-Controller），NAudio 音频管线 |
| 目标框架 | `net10.0-windows10.0.19041.0`（WinForms 混用于安装器/卸载器），`SupportedOSPlatformVersion` 10.0.17763 |
| 仓库 | https://github.com/linx-sys/Aurora （public） |
| 当前版本 | `AppInfo.Version = 3.0.0`；已发布 **Release v3.0.0**（2026-09-10） |
| 分支 | `main` = `develop` = `7b962c1`；tag：`v1.1.0` / `v2.0-stable` / `v3.0.0` |
| 许可 | 代码与全部第三方依赖均 MIT（Concentus 走 `lib/` 本地 DLL） |
| 工作目录 | `D:\WorkSpace\Aurora` |
| 构建入口 | `powershell -ExecutionPolicy Bypass -File build.ps1`（4 步）→ `dotnet test tests/Aurora.Tests.csproj` |

---

## 2. 核心目标与产品定位

**一句话**：漂亮、轻量、本地优先、打开即用的 Windows 本地音乐播放器。

四条贯穿全部技术决策的产品原则：

1. **本地优先**——不强制联网；只有歌词/封面缺失时才自动联网匹配，且可按格式、按数据源关闭。
2. **打开即用**——启动不阻塞、大曲库秒开、双击文件即播、单实例唤起，无登录无向导。
3. **中文场景友好**——老歌 GBK 标签/歌词自动识别不乱码（`CodePagesEncodingProvider` + `Id3.DecodeLoose` 逐级回退）。
4. **音质与听感**——ReplayGain 2.0 响度均衡、预载无缝衔接、可选跨淡、WASAPI 独占（失败自动回退）。

---

## 3. 主要功能

| 域 | 功能 |
| --- | --- |
| **格式支持** | 9 种：MP3 / M4A / FLAC / WAV / AAC / WMA（Media Foundation）+ OGG / OGA（NVorbis 纯托管流式）+ OPUS（Concentus 纯托管）；无临时 WAV、无文件大小上限 |
| **播放与体验** | 常驻混音器管线、剩余 <4s 预载下一首、可选跨淡（0–12s，默认 2s）、播放模式（列表循环/单曲循环/随机+播放轨迹）、音量/静音、快进快退 |
| **媒体库** | SQLite 正式核心存储；启动 DB 秒开 + 后台差分同步；5 种排序（+拖动进入自定义）；搜索（标题/歌手/专辑/文件名，200ms 防抖）；拖放导入；封面缩略图抽屉、拖动排序、删除动画 |
| **歌词与封面** | 同名 `.lrc` 自动加载、逐行高亮滚动、点击行跳转；内嵌封面优先，无封面按歌名生成专属渐变封面（10 组极光配色） |
| **联网匹配** | 酷狗 → 网易云双源降级、标题+歌手打分防翻唱误配、数据源级启停、按格式开关、缓存统计与一键清除（`%LOCALAPPDATA%\Aurora\Cache\`） |
| **音质** | ReplayGain 2.0（EBU R128，参考 -18 LUFS，±12 dB，懒分析并写回 DB）、WASAPI 独占输出、格式归一（48kHz stereo float） |
| **系统集成** | SMTC 媒体键/锁屏/系统媒体浮层、任务栏进度条、JumpList 最近播放（≤8）、9 格式文件关联（`/associate`、`/unassociate`，写 HKCU 免管理员）、单实例（mutex + 命名管道唤醒） |
| **设置与诊断** | 深浅主题、记忆键（文件夹/音量/模式/主题/上次曲目）、音频诊断面板（解码格式/采样率/位深/声道/RG/输出设备快照 + 复制 + 打开日志文件夹） |
| **发行** | Setup 安装包（内嵌依赖 + WinRT 投影，约 30MB）、Portable 自包含单文件、win64.zip、symbols.zip；CI 打 `v*` tag 自动 Build+Test+Release；内置更新检查（24h 节流，不静默替换） |

**快捷键**：`空格` 播放暂停 · `←→` ±5s · `↑↓` 音量 5% · `N/P` 切歌 · `M` 静音 · `Esc` 收起抽屉（搜索框内不触发）。

---

## 4. 技术决策清单（决策 → 原因）

### 4.1 架构与分层

| 决策 | 原因 / 背景 |
| --- | --- |
| **保持 `src/` 扁平单工程**，用命名空间 + 文件头注释表达分层，拒绝拆 6 工程 | 与项目"极简"惯例一致；多工程仅在"接第三方扩展/插件"时再议。`csproj` 设 `EnableDefaultCompileItems=false`，**新增 .cs 必须手工加入 `<Compile Include>`** |
| 五层：View / Application / ViewModel / Service / Model | `MainWindow` 收敛为**装配根**（`FindControls`），业务分入 Controller |
| View 层 Controller 模式（构造注入控件/VM/回调，自挂事件） | 把 909 行的 `MainWindow` 拆到 222 行；`WindowChrome/Theme/Lyrics/Playlist/NetMatch/NetMatchSettings/LibraryImport/Toast/PlaybackTick/Hotkey/WindowMisc/SmTc` + `CommandBindingManager`/`PlaybackStateViewController` |
| **Application 层 `PlaybackCoordinator`** 为播放业务唯一入口 | `MainViewModel` 553 → ~250 行；协调器无 UI 依赖（UI 调度经 `Action<Action>` 注入，测试内联） |
| 接口边界：`IPlaybackService` / `IAudioOutput` / `ILibraryStore` / `ILyricsProvider` | 引擎/库/联网均可注入 fake；**无声卡即可跑引擎测试** |
| 单一数据源：列表 = `PlaylistManager`（Tracks/View 双集合，`RefreshView` 单次 Reset 通知） | 避免双状态；批量通知只发 1 次 Reset（有回归断言） |

### 4.2 播放管线（最核心的技术资产）

| 决策 | 原因 / 背景 |
| --- | --- |
| **常驻混音器**（`MixingSampleProvider` 48k stereo float, `ReadFully=false`），设备只建一次 | 切歌不再重建输出设备，历史上"Play 后 300ms 内 PlaybackStopped"的设备竞态根因消失 |
| **会话 ID（PlaybackSession）** 贯穿 `Load`/`CrossfadeTo` 与结束事件 | 用户快速连切后迟到的自动切歌不再覆盖用户选择；引擎侧过滤 + UI 派发侧二次校验**双防线** |
| **自然结束 = `MixerInputEnded`**，不用设备级 `PlaybackStopped` | 设备级事件仅在主动 Stop/Dispose 时发生，语义更干净 |
| 原"300ms / 200ms / retry×3"经验阈值**已删除** | 被结构化的会话机制取代 |
| 格式归一：多声道取前二（不做下混矩阵）→ 单声道转立体声 → WDL 重采样 48k | 统一混音格式；`GainFadeSampleProvider` 承载 RG 增益 × 淡入淡出包络（包络按已读秒数确定性计算，无定时器） |
| 预载：剩余 <4s（跨淡时 <cf+1.5s）预选并后台预热解码器；手动切歌作废预选 | 无缝衔接；`pendingNext` 保证预热与自动切歌决策一致 |
| 输出设备后台预热 + `EnsureOutput()` 惰性补建；失败置 `_outputFailed` 不再重试 | 启动不阻塞 UI；播放静音降级而不崩溃 |

### 4.3 媒体库与性能

| 决策 | 原因 / 背景 |
| --- | --- |
| SQLite（WAL）**升级为正式核心存储**，非缓存 | 启动秒开 + 差分同步；运行期内存 `PlaylistManager` 仍是唯一可变工作集 |
| **两段式启动**：① `GetByPrefix` + **零探测快路径**物化秒开 → ② 后台文件系统差分同步权威替换 | 10k 首秒开从线性外推 ~13s 降到 **28ms** |
| 指纹 = 文件大小 + `LastWriteTimeUtc.Ticks`，**不做 content hash** | 大库 content hash 成本过高；命中即跳过标签解析 |
| **只缓存"贵"的数据**（标签/时长/内嵌封面/RG）；**lrc 与外部封面每次现读** | 联网匹配会在缓存写入后补上它们，缓存会吃掉更新（刻意决策） |
| 路径规范化 `path_key` + **目录分隔符边界** | `D:\Music` 不得误伤 `D:\MusicBackup`（曾被测试抓出遗漏） |
| 批量上下文：搜索 200ms 防抖、`BatchObservableCollection.ReplaceAll` 单次 Reset、（新增）导入串行队列 + generation token | 减少 UI 通知风暴；防止扫描与拖放导入互相覆盖 |

### 4.4 安装、更新、日志

| 决策 | 原因 / 背景 |
| --- | --- |
| **安装事务化**：`InstallTransaction`（同卷旁路目录 + journal + 备份 + 原子替换），异常退出由下次安装或 `/recover` 回滚 | 原 `WriteOwnedFile` 逐项原地覆盖、清单最后写入，中断会留下无法安全重试的半成品 |
| **安装清单 `InstallManifest`**：`PayloadFiles` 白名单 + 每文件 SHA256；注册表存 `ManifestSha256` 并二次校验 | 卸载只删清单内文件，安装到非空目录不再误删用户文件 |
| `OwnedInstallFile`：按**已打开句柄**校验文件身份（`SetFileInformationByHandle`/`AttributeTag`），拒绝重解析点 | 避免"按路径校验后再删除"的检查/使用分离（TOCTOU） |
| `IsPortable` 由编译期常量 `AURORA_SINGLE_FILE` 决定，不再读 `Assembly.Location` | 单文件发布下该路径不可用 |
| 更新：`JsonSerializer` + DTO 解析、SHA256 摘要校验、临时文件、**不静默替换** | 原正则解析 JSON + 直写目标文件不可靠 |
| 日志：`%LOCALAPPDATA%\Aurora\Logs\`，`aurora.log` 5MB 轮转 + `crash_*.log`（三路钩子；文件名加毫秒 + GUID 防覆盖） | 崩溃日志统一从 `App.LogCrash` 收敛到 `Logger.Crash` |
| Nullable：四工程 `enable`；核心层 26 文件真零警告，**UI 层 21 文件 `#nullable disable` 过渡**（批次 2 待清理） | 控件注入/WinRT/注册表 null 语义密集，收益低风险高 |

### 4.5 工程约定（必须遵守）

- UI 线程切换**只用 `Dispatcher.BeginInvoke`**——`SynchronizationContext.Current` 在 `app.Run()` 前为 null，兜底 Post 会在线程池执行导致跨线程闪退。
- 提交规范：`<type>: 中文摘要`（feat/fix/refactor/docs/test/perf/build/ci）。
- 测试约定：纯逻辑直测、不碰音频设备/WPF 控件；xUnit 测试类**只允许一个公共无参构造**；需覆盖 GBK 链时显式 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`。
- 发布前检查：`build.ps1` 全绿 → `dotnet test` 全绿 → 真机冒烟 → 改 `AppInfo.Version` → 打 tag。

---

## 5. 项目结构（关键路径）

```
D:\WorkSpace\Aurora\
├─ src\                  主程序源码（57 个 .cs，扁平单工程，手工 Compile 列表）
│   ├─ 装配根 / Lifecycle      MainWindow.cs、App.cs、AppInfo.cs
│   ├─ Application 层          PlaybackCoordinator.cs
│   ├─ ViewModel 层            MainViewModel.cs、PlaybackController.cs、PlaylistManager.cs、ViewModelBase.cs、RelayCommand.cs
│   ├─ 播放引擎                PlayerEngine.cs（IPlaybackService）、TrackInputManager.cs、SpectrumCapture.cs、
│   │                          IAudioOutput.cs、WaveOutAudioOutput.cs、WasapiAudioOutput.cs、AudioDecoders.cs、
│   │                          GainFadeSampleProvider.cs、Loudness.cs、IAudioOutput 适配
│   ├─ 解码/标签               OpusWaveReader.cs、VorbisWaveReader.cs、TagReaderService.cs、Tags.cs、Id3.cs、Model.cs
│   ├─ 媒体库                  LibraryDatabase.cs（ILibraryStore）、LibraryImportController.cs
│   ├─ 联网匹配                NetMatch.cs、ILyricsProvider.cs、NetMatchViewController.cs、NetMatchSettingsDialog.cs
│   ├─ 安装/卸载               Installer.cs、Uninstaller.cs、InstallManifest.cs、InstallTransaction.cs、OwnedInstallFile.cs、Assoc.cs
│   ├─ 系统集成                SmTcController.cs、HotkeyController.cs、SingleInstanceServer.cs、UpdateChecker.cs、ThemeController.cs
│   └─ 视图控制器 / 工具       各 *ViewController.cs、UiUtil.cs、Logger.cs、ui.xaml
├─ tests\                22 个测试文件（xUnit；含引擎 fake 注入、性能基线、安装安全/事务、导入回归）
├─ docs\                 ARCHITECTURE / AUDIO_PIPELINE / DATABASE / DEVELOPMENT / PERF_BASELINE / ROADMAP / 本文件
├─ tools\                ui.ps1、shot_*.ps1、repro_switch.ps1、InstallProcessProbe\（安装事务故障注入探针）
├─ experiments\          Spectro.cs（频谱实验，未接入 UI）
├─ assets\               screenshot-main.png、应用图标
├─ lib\                  Concentus 1.1.6 / Concentus.Oggfile 1.0.4（NuGet 未上架）
├─ build\                构建产物（AuroraPlayer、unins、WinRT/SQLite 原生 DLL）
├─ outputs\              会话产物/验证证据（评估报告、TRX、构建日志、安装事务探针结果）
├─ testmusic\            测试用音频样本（mp3/flac/m4a/lrc/jpg）
├─ build.ps1             四步构建：主程序 → 卸载器 → 安装器 → Portable
└─ *.csproj              AuroraPlayer / installer / unins / tests/Aurora.Tests
```

**运行时数据位置**：库 `%APPDATA%\AuroraPlayer\library.db`；缓存 `%LOCALAPPDATA%\Aurora\Cache\`；日志 `%LOCALAPPDATA%\Aurora\Logs\`；安装目标默认 `D:\Applications\Aurora`（当前为 v1.1.0 旧版，建议从 Release v3.0.0 重装）。

---

## 6. 当前进展

### 6.1 版本里程碑

| 版本 | 日期 | 内容 |
| --- | --- | --- |
| v1.1.0 | 2026-09-09 | 首个 CI 自动发布版本（Setup 4.0MB / Portable 73.7MB / win64.zip 91.8MB） |
| **v2.0-stable** | 2026-09-10 | 五项改进后的稳定基线：常驻混音器重构、SQLite 增量扫描、安装器/便携版/CI、9 格式、ReplayGain 2.0、双源降级 |
| **v3.0.0** | 2026-09-10 | 架构重构与产品化收官（Setup 29.6MB / Portable 83.7MB / win64.zip 109.6MB / symbols.zip 48KB） |

### 6.2 工作脉络（按日）

- **09-07 前**：从单文件 `csc` 编译方案演进为 .NET 10 SDK 工程（WPF + MVVM + NAudio/NVorbis/Concentus）。
- **09-08**：7 项代码审查修复（MVVM 收敛、`ILyricsProvider` 抽象、测试项目、NuGet 迁移、死代码清理、空 catch、细节）；新增"联网匹配设置"（格式级开关 + 缓存清除）；提交推送 + 打包静默安装；README 据实修订 5 处不实描述。
- **09-09**：ChatGPT 外部评审（综合 7.2/10）→ **P0**（MainWindow 79.5KB→40.6KB、`PlaybackSession`、CI、测试 39→142）→ **P1** SQLite 增量扫描 → **P2** 音频四件套（常驻混音器 + Gapless + ReplayGain 2.0 + 跨淡 + WASAPI）→ **P3** 产品打磨（Portable/更新检查/崩溃日志/README 重构）→ 修复单曲循环只播一遍 → push 9 提交 + 发布 v1.1.0（首跑 Release 403 因缺 `permissions: contents: write`，修好后需**删远端 tag 重打**才生效）。
- **09-10**：五项改进 `b55a89f`（MainWindow 41.8→24.9KB、`IPlaybackService`/`ILibraryStore`、DB 转正式存储、`IAudioOutput` 注入、SMTC/JumpList/更新体验、搜索防抖）；`docs/ROADMAP.md` 据实修订；**阶段 0~11 全部完成**并发布 **v3.0.0**。
  - 关键量化：MainWindow **909 → 222 行**；PlayerEngine **586 → 468 行**；MainViewModel **553 → ~250 行**；测试 **39 → 142 → 173 → 191 → 242 → 259 → 389**；10k 首秒开 **~13s（外推）→ 28ms**。
- **09-11（今日）**：
  1. **只读静态评估**（12 KB 报告，`outputs/Aurora-本地音乐播放器项目评估报告.md`）：分维度评级 8/8/8/7.5/8/6.5/5.5，给出 P0×2 / P1×10 / P2 若干，含证据行号与建议。
  2. **续办修复 + 全量验证**（`outputs/Aurora-validation-2026-09-11.txt`，13:15）：补齐 `FromMetadata` 取消参数、六种排序 API、清单测试访问权限；列表 HashSet 去重 + 单次 Reset + 自引用快照；响度分析复用串行队列并给 `Completion`；窗口关闭经 Dispatcher 延后。当时记录为 **371/371 通过**、重点复验 110/110 —— 但这是 14:09 之前的用例集，**已过时**（见下一节）。
  3. **安装事务与安全链路落地**（13:49–14:26，**已提交** `d1aa138`）：`InstallManifest` / `InstallTransaction` / `OwnedInstallFile` 三个新文件 + `Installer`/`Uninstaller` 重写 + `/recover`；`LibraryDatabase` 引入 `path_key` 分隔符边界；`UpdateChecker` 改 JSON DTO + SHA256；`UpdateCheckerTests`/`SettingsRegressionTests`/`InstallSafetyTests`/`InstallTransactionTests`/`ImportRegressionTests` 五个新测试文件；`InstallProcessProbe` 故障注入探针实测各阶段进程终止后 `rollback_exact=true`（当时 48 个安装事务测试通过）。

### 6.3 本批修复的收尾与真机验证（2026-09-11 下午，已完成）

按"提交推送 → clean 全量构建+测试 → 真机四条路径 → 文档校准"推进，结论如下。

**① 提交与推送**：`develop` 两个提交已推送（`d1aa138` 修复批次 + `658c40c` 安装事务重命名修复）。
仓库无未提交改动；`.gitignore` 补 `.workbuddy-ai/`、`outputs/`（本地会话与验证产物不入仓库）。

**② clean 全量构建**：Release + Debug × 4 工程（AuroraPlayer / unins / installer / tests）共 8 组合
`-t:Rebuild -warnaserror` **全部 0 警告 0 错误**（`outputs/build-matrix.json`）。此前"编译警告仍存在"的记录作废。

**③ 全量测试真实结果：389 通过 / 0 失败 / 0 跳过**（`outputs/aurora-regression-rerun.trx`）。
⚠️ 复验推翻了两个既有结论：
- 用例总数不是 371 而是 **389**（371 是 14:09 之前、最后五个测试文件加入之前的旧数，14:26 的最后一次改码从未被验证过）；
- 首次 clean 复验有 **15 个失败**（安装事务 15/16），根因是新发现的产品缺陷（见 §9.2）。

**④ 真机验证（隔离目录完成，未触碰 `D:\Applications\Aurora`）**——脚本与报告在 `outputs/machine-verify/`：

| 阶段 | 场景 | 结果 |
| --- | --- | --- |
| 1 | 全新静默安装（22 文件/清单哈希/注册表登记/无关联写入） | **47/47 通过** |
| 1 | 覆盖安装；无清单非空目录拒绝（旧版目录升级场景）；已改动文件拒绝；卸载只删清单内文件并保留用户文件与目录 | 同上 |
| 2 | **真升级 3.0.0 → 3.0.1**（临时构造升级包，源码已还原）：版本/哈希替换、旧文件留 `.old` 备份、注册表同步、用户文件保留、升级后卸载干净 | **18/18 通过** |
| 3 | **中断恢复**：准备期强杀 → `/recover` → 同目录重装；提交期强杀 → 直接重装（内部先回滚）；连续两次中断后重装；中断目录 `/recover` 兜底 | **21/21 通过** |

安全前提：测试期间备份并在结束时还原了 HKCU 卸载注册表项；静默安装带 `/noassoc /nodesktop`，
未改动文件关联与桌面；**你现有的 `D:\Applications\Aurora` 安装全程未被触碰**。
未覆盖：图形安装向导的交互页面点击、真实声卡/设备拔插、DPI 与多显示器（见 §8.1）。

**⑤ 文档校准**：统一"测试数字口径"（当前状态类文档用实测值，历史条目保留当时值并标注），
写入 CHANGELOG 口径说明，同步 README / ARCHITECTURE / DEVELOPMENT / ROADMAP / 本文档。

### 6.4 v3.0.1 发布记录（2026-09-13，已完成）

- 版本号 3.0.0 → **3.0.1**（`src/AppInfo.cs`）；CHANGELOG `[Unreleased]` 定稿为 `[3.0.1] - 2026-09-13`。
- 提交 `4874694`（release: v3.0.1——安装链路与状态一致性修复批次，含 `tools/machine-verify/` 三个真机验证脚本）。
- 发布前检查：四步构建（主程序/卸载器/安装器/Portable）0 警告 0 错误；全量测试 **389/389**；
  产物实测安装：清单与注册表 `DisplayVersion` 均为 3.0.1、22 个载荷文件齐全。
- `develop` 推送 → `main` **fast-forward** 合并 → 附注 tag `v3.0.1` 推送。
- CI：`main` push 运行 success；tag 运行 11 个步骤全部 success（构建/测试/双 publish/收产物/建 Release）。
- Release 产物（https://github.com/linx-sys/Aurora/releases/tag/v3.0.1）：
  `AuroraPlayer-Setup.exe` 29.7 MB · `Aurora-x64-Portable.exe` 83.6 MB · `Aurora-win64.zip` 109.7 MB · `Aurora-symbols.zip` 61.9 KB。
- ⚠️ 升级路径：本机既有安装 `D:\Applications\Aurora`（v3.0.0，**无安装清单**）需**先卸载**再安装 v3.0.1，
  或安装时选择新的专用空目录（见 §9.1）。
- 环境备注：本机 Bash 子进程缺 `APPDATA` / `ProgramFiles` / `ProgramW6432` 等变量，
  直接跑 dotnet 会报 NuGet `Value cannot be null. (Parameter 'path1')`，需显式补齐后再构建（见 §9.3）。

### 6.5 ⚠️ 历史状态（保留供追溯）

- `main` 与 `develop` 曾停在已发布的 `7b962c1`（v3.0.0），工作区曾有 **47 个文件修改（+2448 / −1142）+ 9 个未跟踪文件**；
- 那批改动**现已提交并推送**（`develop` 至 `658c40c`），不再有"未提交修复"。
- 根目录的安装包/Portable 产物是本地构建产物（git 忽略），**不代表已发布的 v3.0.0**。

---

## 7. 质量与验证现状

| 维度 | 状态 |
| --- | --- |
| 单元测试 | **389 通过 / 0 失败 / 0 跳过**（2026-09-11 复验，`outputs/aurora-regression-rerun.trx`；22 个测试文件）。覆盖引擎竞态/跨淡/预载/RG、库差分与目录边界、ID3/GBK、LRC、安装清单与事务、安装安全、导入并发、更新解析、设置回退、性能基线 |
| 构建 | Release + Debug × 4 工程 clean 重建（`-t:Rebuild -warnaserror`）**8/8 零警告零错误** |
| 真机 | 全新安装 / 覆盖升级（3.0.0→3.0.1）/ 卸载 / 中断恢复（强杀 + `/recover` + 重试）**86/86 项检查通过**（阶段 1 47 + 阶段 2 18 + 阶段 3 21） |
| 性能基线 | `PerformanceBaselineTests`（Release）：UpsertMany 10k ≈ 195ms、GetByPrefix 10k ≈ 38ms、**零探测秒开 10k ≈ 28ms** |
| CI | GitHub Actions：push/PR → restore/build/test；tag `v*` → 四产物 Release（symbols.zip 于 3.0 阶段加入） |
| 真机未覆盖 | 图形安装向导交互、真实声卡与设备拔插、DPI/多显示器、SMTC 实机、长期运行稳定性 |

---

## 8. 待办事项

### 8.1 立即（发布前必修）

| # | 事项 | 状态 |
| --- | --- | --- |
| 1 | 提交并推送修复批次 | ✅ 已完成（`develop` → `658c40c`） |
| 2 | clean 全量构建 + 全量测试复验 | ✅ 已完成：8/8 零警告零错误；**389/389 通过**（过程中查出并修复安装事务重命名缺陷） |
| 3 | 真机安装/升级/卸载/中断恢复 | ✅ 已完成（隔离目录，86/86 项检查通过；**未触碰你现有的 `D:\Applications\Aurora`**） |
| 4 | 文档测试数字口径校准 | ✅ 已完成（CHANGELOG 写明口径；README/ARCHITECTURE/DEVELOPMENT/ROADMAP/本文档同步为 389） |
| 5 | **发布决策** | ✅ 已完成：2026-09-13 发布 **v3.0.1**（`main` = `develop` = `4874694`，CI 四产物齐全） |
| 6 | 图形安装向导交互回归 | ⬜ 未做：本次只验证了静默安装路径（与图形路径共用 `RunInstall`），向导页面点击需人工过一遍 |
| 7 | 真实声卡/设备拔插、DPI/多显示器、SMTC 实机 | ⬜ 未做 |
| 8 | 你现有 `D:\Applications\Aurora`（v3.0.0，**无安装清单**）的迁移 | ⬜ 需先卸载再安装新版本（或装到新目录）——见 §9.1 |

### 8.2 近期（发布质量）

- Nullable **批次 2**：清理 21 个 `#nullable disable` UI 文件（`MainWindow`/各 Controller/`MainViewModel`/`SmTc`/`Assoc`/`Installer`/`Uninstaller`/`Tags`/`Id3`/`Lrc`）。
- 空 `catch` 分级：至少记录操作/对象/异常，数据安全路径禁止静默吞错。
- 性能基线补齐 P50/P95 + 机器信息；GUI 冷/热启动、内存、CPU 三场景实测（`PERF_BASELINE.md` 占位表待填）。
- 长音频 ReplayGain 改为流式块统计（现状累积整首样本到 `List<float>`，内存与时长成正比）。
- 多声道按通道布局做标准下混（5.1/7.1 中心/环绕/LFE 权重），并在诊断中说明策略。
- 合成大库端到端验证（10 万行 DB + GUI 实测 <2s）。

### 8.3 长期（演进方向）

- 播放队列 / 收藏 / 播放历史（DB 扩展表 `playlists`/`favorites`/`history` 属产品功能，**待立项时一并设计，不提前建空表**）。
- 频谱可视化接入（管线已内置 PCM 抓取与 `PullSpectrum()`，仅需渲染层消费）。
- 设置模型类型安全化 + schema 版本迁移（现状为字符串 KV）。
- Composition Root 按域拆分（播放器/媒体库/系统集成），降低控制器互相回调耦合。
- 多工程拆分——**仅在开始接第三方扩展/插件时再考虑**。

---

## 9. 边界情况与已知问题

### 9.1 功能/能力边界（设计取舍，非缺陷）

- **旧版安装目录不支持原地覆盖升级**：安装改为清单驱动（`InstallManifest`）后，安装器只接管带有效清单的目录；
  目标目录非空且无清单时**明确拒绝**并提示选择新的专用空目录。因此从 **v3.0.0 及更早**升级必须先卸载或换目录
  ——你机器上现有的 `D:\Applications\Aurora`（v3.0.0，无清单）正属此列。
- 安装/升级会在**安装目录同级**保留 `.AuroraInstall-<sha256>` 审计目录（事务日志 + 旧文件备份 + 属主与锁文件），
  卸载不会递归删除它。全新安装时该目录只含日志，属可见遗留物——建议后续加"无备份且已提交/已卸载即清理"的策略。
- 已登记文件被修改后会**阻止重装**（安全设计）。恢复方式：删除该文件后重装即可（真机已验证）。
- `.aac`（ADTS）与 `.wma` 无容器标签解析：标题取自文件名，封面不显示；`.aac` 时长为 ADTS CBR 估算。
- ReplayGain 为**懒分析**：每首首次播放时后台分析，完成后才应用增益（结果写回 DB 复用）。
- 随机播放"上一首"仅本会话有效；跨淡排除单曲循环。
- 更新检查**只提示/下载并唤起安装器**，不静默替换程序文件；便携版直接跳发布页。
- 频谱 UI 未启用；`Spectro.cs` 为实验代码。
- "无缝衔接"目前是**预载式近无缝**，非采样级 gapless 拼接。

### 9.2 静态评估指出的问题及当前状态

> 状态口径：**已修复** = 代码改完 + 单元测试覆盖；**已修复·真机已验证** = 另有 2026-09-11 真机检查佐证（§6.3）。

| 编号 | 问题 | 当前状态 |
| --- | --- | --- |
| P0-1 | 安装到非空目录后卸载可能删除无关文件 | **已修复·真机已验证**（`InstallManifest` 白名单 + 注册表哈希校验 + 卸载只删清单内匹配文件；真机确认用户文件与目录保留） |
| P0-2 | 静默安装 `/S /D=` 绕过目录安全校验 | **已修复·真机已验证**（校验下沉到 `RunInstall`/`ValidateInstallDirectory`；黑名单路径与根目录被拒） |
| ★新 | **安装事务重命名缺 NUL 终止符**（本次 clean 复验新发现） | **已修复·真机已验证**（补终止符 + 扩展长度路径；此前深层目录会产生乱码文件名或报 206） |
| P1-1 | `GetByPrefix` 相邻目录污染（`D:\Music` vs `D:\MusicBackup`） | **已修复**（`path_key` + 分隔符边界；测试覆盖大小写与末尾分隔符） |
| P1-2 | 播放失败时引擎 / 协调器 / UI 状态不一致 | **已修复**（失败状态与输出工厂失败场景纳入协调器与测试） |
| P1-3 | 非法 `mode` 等设置值可致启动异常 | **已修复**（`Settings.GetEnum` 白名单回退 + `SettingsRegressionTests`） |
| P1-4 | 排序按钮无法进入"自定义顺序" | **已修复**（`% SortModeCount` 覆盖 6 种模式，名称同步） |
| P1-5 | 随机播放历史未在模式切换/删除时维护 | **已修复**（`PlaybackController` 模式变更逻辑 + 删除清理） |
| P1-6 | 目录扫描与拖放导入并发覆盖播放列表 | **已修复**（串行队列 `Enqueue` + `generation` token + `ImportPathsAsync`） |
| P1-7 | 扫描异常时 `isLoadingDir` 永久为 true | **已修复**（后台主体 `try/finally` + `lifetime` CTS） |
| P1-8 | SMTC 初始化时机 / 缩略图同步等待 `.Wait(500)` | **已修复**（`SmTcController` 重写）；SMTC 实机未验证 |
| P1-9 | 更新器正则解析 JSON、直写目标文件 | **已修复**（JSON DTO + SHA256 摘要 + 临时文件 + `UpdateCheckerTests`） |
| P1-10 | 崩溃日志路径与 Logger/文档不一致 | **已修复**（`App.LogCrash` → `Logger.Crash`；文件名加毫秒+GUID） |
| P2 | `#nullable disable`、空 `catch`、控制器耦合、Settings 无类型安全、文档数字漂移 | **部分处理**：文档数字口径已校准（§8.1 #4）；其余见 §8.2/§8.3 |

### 9.3 工程陷阱（踩过的坑，避免重犯）

- **对同一文件的多个 `Edit` 不能并行下发**——后面的编辑基于旧快照，前面的会静默回滚，表现为"编辑成功但文件是旧内容"（曾浪费多轮排查）。同文件多处改动必须串行。
- 大重构后必须**跑一次真机启动冒烟**：`dotnet test` 不覆盖 UI 装配路径（曾因"实例方法组绑定尚未赋值的字段"导致启动崩溃，`FindControls` 漏查控件是第二处）。
- **装配根中把"后构造控制器的实例方法"传给"先构造控制器"时必须用 lambda**（方法组在传参瞬间绑定 target，会抛 `ArgumentException`）。
- **tag 触发的工作流用的是 tag 指向提交上的 workflow 文件**——改 workflow 后必须删远端 tag 再重打。
- 引擎公开语义变化后要**全量审查调用点的隐含假设**（"自然结束后 reader 仍可用"在旧架构成立、新架构不成立 → 单曲循环只播一遍）。
- 排查日志先分辨**测试进程与播放器共用 `aurora.log`**（`aurora_rg_*` 前缀只出现在 tests/，曾被误判为播放器 bug）。
- 测试 flake：硬切后拉取线程仍活跃，"Position==0"类断言与活跃拉取天然竞争 → 改断言"位置归属新曲目"。
- 环境类：播放器运行中会锁 `build\AuroraPlayer.exe`，验证构建用 `-p:OutputPath=obj/verify/` 旁路；本机 `dotnet` 需用用户级 `C:\Users\Jinwei\AppData\Local\Microsoft\dotnet\dotnet.exe`（PATH 里的只有运行时）；`github.com` 间歇断流，push 失败等 2~3 分钟重试，且失败时可能误报 "Everything up-to-date"，以 `git log origin/develop` 为准；NAudio 的 `MixingSampleProvider`/`WdlResampling`/`SampleProviderEventArgs` 命名空间是 `NAudio.Wave.SampleProviders`。
- **`FILE_RENAME_INFO` 缓冲区必须留 NUL 终止符**：内核按终止符读文件名字符串，只给 `FileNameLength` 会读进分配区外的内存，
  文件名尾部出现乱码（本轮实测 `AuroraPlayer.exeon`）；同时原生 API 不享受 BCL 长路径支持，
  路径须显式加 `\\?\`，否则 `SetFileInformationByHandle` 在超 MAX_PATH 时报 206。
  这类坑只有"真跑一遍"才能发现——`dotnet test` 全绿也可能只是没覆盖到最后一次改码。
- **测试目录自身会构成边界条件**：安装事务用例的目录在 `tests/bin/.../transaction-case-*/` 下，
  叠加 `.AuroraInstall-<64 位 sha256>` 后很容易越过 MAX_PATH——这是真实用户深层安装路径的等价场景，
  不要为了"让测试过"去缩短测试路径。
- **改完最后一行代码必须重跑全量测试**：本批 14:26 的最后一次改码晚于 14:09 的全绿运行，
  于是"371/371 通过"的记录实际是过时结论，缺陷一直潜伏到本次 clean 复验才暴露。
- **本机 Bash 子进程缺 Windows 目录环境变量**：缺 `APPDATA` / `ProgramFiles` / `ProgramFiles(x86)` / `ProgramW6432` 时，
  `dotnet build/restore` 会报 `Value cannot be null. (Parameter 'path1')`（NuGet.targets）。
  用 `env APPDATA='C:\Users\Jinwei\AppData\Roaming' ProgramFiles='C:\Program Files' ... dotnet ...` 补齐即可，
  不必改系统环境（`outputs/verify_builds.py` 与 `tools/machine-verify/*` 都自带这套变量）。
- **本机 `git fetch` 不写入 `origin/*` 远程跟踪引用**（命令报告 `[new branch] ... -> origin/develop`，
  但 `.git/refs/remotes/origin/` 始终为空，`git log origin/develop` 会报 ambiguous 参数）。
  验证远端状态请改用 `git ls-remote origin refs/heads/develop`，或用 `git push` 回显的 `<old>..<new>` 区间确认。
  另：Bash 工具禁止调用 `powershell.exe`（安全策略），发布构建改用与 `build.ps1` 等价的四条 dotnet 命令。

---

## 10. 术语与速查
| 项 | 值 |
| --- | --- |
| 构建 | `powershell -ExecutionPolicy Bypass -File build.ps1`（主程序 → 卸载器 → 安装器 → Portable） |
| 测试 | `dotnet test tests/Aurora.Tests.csproj`（全量，无声卡可跑）；性能：`-c Release --logger "console;verbosity=detailed"` |
| 关键路径 | 库 `%APPDATA%\AuroraPlayer\library.db`｜缓存 `%LOCALAPPDATA%\Aurora\Cache\`｜日志 `%LOCALAPPDATA%\Aurora\Logs\` |
| 关联命令 | `AuroraPlayer.exe /associate`、`/unassociate`；安装 `Setup.exe /S "/D=路径"`（注意 `& exe /S "/D=path"` 直接调用才生效） |
| 命名管道 | `AuroraPlayer.Instance`（单实例唤醒） |
| 核心常量 | 参考响度 −18 LUFS｜RG 增益上限 +12 dB｜门限 −70 LUFS / −10 LU｜混音 48kHz stereo float｜预载窗口 4s｜跨淡默认 2s（0–12s）｜搜索防抖 200ms｜更新节流 24h｜日志轮转 5MB｜JumpList ≤8 条 |
| 文档索引 | 架构 → `docs/ARCHITECTURE.md`｜管线 → `docs/AUDIO_PIPELINE.md`｜库 → `docs/DATABASE.md`｜开发 → `docs/DEVELOPMENT.md`｜性能 → `docs/PERF_BASELINE.md`｜路线图 → `docs/ROADMAP.md`｜产品页 → `README.md`｜变更 → `CHANGELOG.md` |
| 验证脚本（本地，`outputs/` 不入仓库） | 构建矩阵 `outputs/verify_builds.py`｜安装/卸载 `outputs/machine-verify/verify_install_stage1.py`｜升级 `verify_upgrade_stage2.py`｜中断恢复 `verify_interrupt_stage3.py`｜安装事务故障注入 `tools/InstallProcessProbe`（已入仓库） |

---

*本文档为记忆整合产物；后续每轮重要工作应追加到 `.workbuddy/memory/YYYY-MM-DD.md`，并在此处同步"当前进展 / 待办 / 已知问题"三节。*
