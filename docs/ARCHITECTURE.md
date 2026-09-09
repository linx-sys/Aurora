# Aurora 架构与设计文档

> 面向开发者/维护者的技术细节。产品功能见 [README](../README.md)。

## 技术栈

- **目标框架**：net10.0-windows（WPF + WinForms 混用：播放器 WPF，安装器/卸载器 WinForms）
- **音频**：NAudio 2.2.1（MIT）— 常驻混音器管线 + WaveOutEvent / WASAPI 独占输出
- **解码**：Media Foundation（mp3/flac/m4a/aac/wav/wma）+ NVorbis 0.10.4（ogg/oga）+ Concentus 1.1.6（opus，NuGet 未上架，本地 DLL）
- **媒体库缓存**：Microsoft.Data.Sqlite 10.0.12（MIT）
- **测试**：xUnit，`dotnet test tests/Aurora.Tests.csproj`（173 用例）

## 架构分层

```
┌────────────────────────────────────────────────────┐
│ View 层（ui.xaml BAML + MainWindow.cs）             │
│  • 核心状态/命令 Binding 到 ViewModel               │
│  • View-specific Controllers（View 行为不进 VM）：  │
│    WindowChromeController  Win32 圆角/最大化/最小尺寸│
│    ThemeController         主题注入/切换/持久化      │
│    LyricsViewController    歌词渲染/高亮/滚动/配色   │
│    PlaylistViewController  抽屉/拖动排序/删除/计数   │
│    NetMatchSettingsDialog  联网与播放设置对话框      │
│    LibraryImportController 目录扫描/导入/播放恢复    │
│  • MainWindow 保留装配、播放状态 UI、进度计时、Toast │
├────────────────────────────────────────────────────┤
│ ViewModel 层                                        │
│  • MainViewModel     协调器（引擎/模式/列表/更新检查/│
│                       ReplayGain 调度/预载决策）     │
│  • PlaybackController 播放模式状态机（纯逻辑可测试） │
│  • PlaylistManager   Tracks/View 双集合 + 批量刷新  │
├────────────────────────────────────────────────────┤
│ Service 层                                          │
│  • PlayerEngine   常驻混音器播放引擎（见下）         │
│  • AudioDecoders  解码器工厂 + 混音格式归一化        │
│  • Loudness       ReplayGain 2.0 / EBU R128 分析    │
│  • TagReaderService 统一标签读取（9 格式 + 文件名   │
│                     推断 + 下载器占位标签清除）      │
│  • NetMatch + ILyricsProvider 联网匹配（酷狗→网易云  │
│                     降级链，可插拔）                 │
│  • LibraryDatabase SQLite 媒体库缓存（增量扫描）     │
│  • SingleInstanceServer 命名管道单实例唤醒           │
│  • UpdateChecker   GitHub Releases 更新检查         │
│  • Logger          应用日志 + 崩溃日志               │
├────────────────────────────────────────────────────┤
│ Model 层                                            │
│  • Model.cs（Track / Library 扫描与增量构建 /       │
│    Settings INI）                                   │
│  • Id3.cs / Tags.cs 底层标签解析（UTF-8→GBK 宽松解码）│
└────────────────────────────────────────────────────┘
```

## 播放管线（P2 重构后）

```
WaveOutEvent（常驻，150ms/4 buffers）
  ← SampleCaptureProvider        频谱采样（混音器输出）
  ← MixingSampleProvider         48kHz stereo float，ReadFully=false
     ← 曲目输入 ×N
        解码器（WaveStream）
        → 多声道>2 截断前二 → 单声道转立体声 → WDL 重采样 48k
        → GainFadeSampleProvider（ReplayGain 增益 × 淡入淡出包络）
```

关键语义：

- **设备只创建一次**。切歌不再重建 WaveOut——历史上"Play 后 300ms 内 PlaybackStopped"的设备竞态根因消失（原 300ms/200ms/retry×3 经验阈值已删除）。
- **自然结束** = `MixerInputEnded` 携带会话 ID；跨淡切换时旧输入的结束事件因会话不符被忽略。
- **会话 ID（PlaybackSession）**：每次 `Load()`/`CrossfadeTo()` 递增；所有异步结束事件在触发源处捕获所属会话，过期事件直接丢弃。引擎侧过滤 + UI 派发侧二次校验双防线。
- **预加载（Gapless）**：`MainViewModel.PreloadNextIfNearEnd()` 在剩余 <4s（跨淡时 <cf+1.5s）时预选下一首并后台预热解码器；`pendingNext` 保证预热与自动切歌决策一致；手动切歌作废预选。
- **跨淡（Crossfade）**：剩余 ≤2s（可配置）时新输入淡入混入、旧输入淡出后定时移除。
- **格式归一**：混音器固定 48kHz stereo float；多声道取前二（不做下混矩阵）、单声道转立体声、WDL 重采样。

## ReplayGain 2.0（Loudness.cs）

- K-weighting：RBJ 双二阶（高架 1681.97Hz +3.9998dB Q0.7071 + 高通 38.135Hz Q0.5003），任意采样率。
- 门限响度：400ms 块 / 100ms 步；绝对门 -70 LUFS，相对门 -10 LU。
- 增益 = 参考 -18 LUFS - 综合响度；`LinearFor(gain, peak)` 按采样峰值钳制防削波（增益上限 +12dB）。
- 校准：单声道 1kHz 满幅正弦（44.1kHz）≈ -3.05 LUFS（对齐 pyloudnorm 权威测试值）。
- 懒分析：曲目首次播放时后台全解码分析，结果写 SQLite（track_gain/track_peak），完成后实时应用；下次播放直接读缓存。

## 媒体库缓存与增量扫描（P1）

- 库文件：`%APPDATA%\AuroraPlayer\library.db`（WAL）。
- 指纹 = 文件大小 + LastWriteTimeUtc.Ticks（不做 content hash）。命中 → 复用元数据（跳过标签解析）；未命中 → 解析并回写。
- 只缓存"贵"的数据（标签/时长/内嵌封面）；**lrc 与外部封面兜底每次现读**——联网匹配在缓存写入后出现，缓存会吃掉更新。
- 换目录扫描清理已消失文件的过期行（目录分隔符边界匹配，`D:\Music` 不误伤 `D:\MusicBackup`）。

## 单一数据源原则

- 播放列表唯一数据源 = `PlaylistManager`（Tracks/View 双集合，`RefreshView` 批量单次 Reset 通知）。
- 播放业务唯一入口 = `MainViewModel.PlayTrack` / `DeleteTrack`；UI 层通过 `CurrentTrackChanged` 事件联动。
- 搜索/排序管道唯一（`SearchText`/`SortMode` setter 即时刷新）。

## UI 线程安全

`PlayerEngine` 事件在后台线程触发；`MainViewModel` 捕获 `Dispatcher.CurrentDispatcher` 并 `BeginInvoke` 切回 UI 线程。**不能**用 `SynchronizationContext.Current`——`app.Run()` 之前它是 null，兜底 `new SynchronizationContext()` 的 Post 会在线程池执行导致跨线程闪退。

## 中文兼容

- `.NET Core/10` 默认无代码页编码：启动时注册 `CodePagesEncodingProvider`（GBK）。
- `Id3.DecodeLoose`：UTF-8 严格解码 → GBK → Latin1 逐级回退；BOM 处理。

## 文件关联

写 `HKCU\Software\Classes`（无需管理员）；`Assoc.IsAssociated()` 检测 UserChoice 覆盖，失效时引导跳转 `ms-settings:defaultapps`。Windows 10/11 对默认应用的保护：已有占用时以"选择打开方式"提示一次。

## 安装器 / 发布

- **Setup**（installer.csproj）：WinForms 单文件（PublishSingleFile），内嵌播放器/卸载器/全部依赖 DLL；支持 `/S /D=路径` 静默安装（含引号/空格边界处理）；写 HKCU 卸载信息。
- **Portable**：`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`，自包含单 exe，免安装免运行时。
- **更新检查**：GitHub Releases API，24h 节流，语义化版本数值比较；仅提示 + 打开发布页，不自动替换文件。

## 日志

- 应用日志 `%LOCALAPPDATA%\Aurora\Logs\aurora.log`（5MB 轮转 aurora.old.log）
- 崩溃日志 `crash_*.log`（DispatcherUnhandledException / AppDomain.UnhandledException / Main 三路钩子，含版本/OS/完整堆栈）

## 测试

`tests/` 173 用例：PlaybackController（模式/随机轨迹）、PlaylistManager（排序/搜索/批量通知）、Lrc（多时间标签/offset/IndexAt）、Id3（v1/v2.2/2.3/2.4、GBK 回退、封面、损坏容错）、Library（扫描/lrc 配对/文件名推断）、LibraryDatabase + 增量扫描（指纹复用/失效重解析/清理边界）、Loudness（校准/门限/频响）、GainFade（包络）、NetMatch 设置、Duration（合成帧流）。

> 注意：测试进程需显式 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` 才能覆盖 GBK 链。
