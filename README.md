# Aurora · 极光音乐

一个纯粹、好用的 **Windows 本地音乐播放器**。基于 **.NET 10 LTS + WPF**，采用 **MVVM 分层架构**，音频输出使用 **NAudio**，OGG/OPUS 使用 **NVorbis** 纯托管解码。全部第三方依赖均为 **MIT 许可证**。

## 成品

**`AuroraPlayer-Setup.exe`（约 1.9 MB，单文件安装包，框架依赖部署）**

> 安装包内嵌播放器、卸载器及全部第三方 DLL，但**不内嵌 .NET 10 运行时**。目标系统需安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（Win10 1607+ / Win11 可通过系统更新或手动安装）。若未安装运行时，播放器启动会失败并提示"找不到托管 DLL"，请先安装运行时。

- 双击运行 → 图形化向导 → **可自由选择安装目录**（Win11 风格文件夹浏览器，默认安装到用户目录，无需管理员权限）
- 安装选项：创建桌面快捷方式、**一键关联全部 9 种音频格式**或按需勾选（3×3 复选框矩阵）
- 自带卸载器（控制面板"应用和功能"或开始菜单均可卸载，音乐文件不会被删除）
- 支持静默安装：`AuroraPlayer-Setup.exe /S /D=D:\你的目录`

## 支持格式

| 格式 | 解码方式 | 标签/封面 |
| --- | --- | --- |
| `.mp3` | NAudio / Media Foundation | ID3v1/v2.2/v2.3/v2.4 + 封面 |
| `.m4a` | NAudio / Media Foundation | iTunes ilst + 封面 |
| `.aac` | NAudio / Media Foundation | 裸流无容器标签：标题取自文件名，时长为 ADTS 帧头 CBR 估算 |
| `.flac` | NAudio / Media Foundation | VORBIS_COMMENT + 内嵌封面 |
| `.wav` | NAudio / Media Foundation | LIST INFO / id3 chunk |
| `.wma` | Media Foundation | 无 ASF 标签解析：标题取自文件名，封面不显示 |
| `.ogg` / `.oga` | **NVorbis 纯托管内置解码** | VorbisComment |
| `.opus` | **Concentus 纯托管内置解码** | VorbisComment |

> OGG 播放采用 [NVorbis](https://github.com/skolima/NVorbis)（**MIT**）纯托管解码器，通过 `VorbisWaveReader` 直接流式接入 NAudio 播放链（`WaveStream` + `ISampleProvider`），**无需先解码为临时 WAV 文件**，无磁盘 I/O、无切换延迟、无临时文件泄漏，支持任意大小文件。OPUS 播放采用 [Concentus](https://github.com/lostromb/concentus) + Concentus.OggFile（**MIT**）纯托管解码器，通过 `OpusWaveReader` 以同样方式流式接入（Media Foundation 不支持裸 .opus 字节流，无法依赖系统解码）。

## 架构

```
┌─────────────────────────────────────────────────┐
│  View 层 (ui.xaml + MainWindow.cs)              │
│  • BAML 编译期 XAML，核心属性/命令 Binding 到 VM  │
│  • UI 渲染：歌词滚动/黑胶动画/主题/进度条/音量条  │
│  • UI 交互逻辑（code-behind）：拖拽排序/快捷键/    │
│    拖放导入/窗口控制/播放列表面板交互              │
├─────────────────────────────────────────────────┤
│  ViewModel 层                                    │
│  • MainViewModel    协调器，统筹各服务与控制器    │
│  • PlaybackController 播放模式状态机（循环/单曲/  │
│                     随机），纯逻辑可单元测试       │
│  • PlaylistManager  播放列表管理（增删/排序/搜索/ │
│                     拖动排序/去重）                │
│  • ViewModelBase/RelayCommand MVVM 基础设施      │
├─────────────────────────────────────────────────┤
│  Service 层                                      │
│  • PlayerEngine     NAudio 播放引擎 + SampleCapture│
│                     Provider 真实频谱样本抓取      │
│  • VorbisWaveReader NVorbis OGG/OPUS 流式解码器  │
│                     （WaveStream + ISampleProvider）│
│  • TagReaderService 统一标签读取（9 种格式，     │
│                     含 ADTS AAC 时长估算）        │
│  • NetMatch         联网匹配编排层（缓存管理 +   │
│                     数据源注册表 + 降级链）        │
│  • ILyricsProvider  歌词/封面提供者接口 + 基类   │
│                     （酷狗/网易云独立实现，       │
│                       失效自动降级到下一源）       │
├─────────────────────────────────────────────────┤
│  Model 层 (Model.cs / TrackMetadata)             │
│  • 数据模型、目录扫描、设置持久化（SettingsStore） │
│  • Id3.cs / Tags.cs 底层标签解析                 │
└─────────────────────────────────────────────────┘

experiments/  实验性代码（不参与编译）
  • Spectro.cs  WASAPI 环回采样原型（已被 PlayerEngine
                内置 SampleCaptureProvider 取代）
```

**关键设计决策**：
- **MVVM 单一数据源**：播放状态（tracks/view/current/mode/volume/playing/sortMode）全部收敛在 `MainViewModel` 与 `PlaylistManager`，`MainWindow.cs` 只保留对 VM 状态的只读代理与 UI 联动（图标/动画/提示），不再维护任何平行的状态副本；搜索/排序管道唯一（`PlaylistManager.RefreshView`），列表计数由 `View.CollectionChanged` 统一驱动
- **播放模式状态机抽离**：`PlaybackController` 为纯逻辑类（不触碰 UI/引擎），配合 `PlaylistManager` 一起被单元测试覆盖（`tests/`，39 个用例全部通过）
- **UI 线程安全调度**：`PlayerEngine` 的 `StateChanged`/`PlaybackEnded` 事件在后台线程触发，`MainViewModel` 捕获 `Dispatcher.CurrentDispatcher` 并通过 `Dispatcher.BeginInvoke` 统一切回 UI 线程（不能用 `SynchronizationContext.Current`——`app.Run()` 之前它是 null，兜底 `new SynchronizationContext()` 的 Post 会在线程池执行导致跨线程闪退）
- **OGG 流式播放**：`VorbisWaveReader`（继承 `WaveStream` + 实现 `ISampleProvider`）直接将 NVorbis 解码的 PCM 浮点样本接入 NAudio 播放链，**无需先解码为临时 WAV**，消除磁盘 I/O、切换延迟与临时文件泄漏；支持任意大小文件（无 50MB 上限）
- **XAML BAML 编译**：ui.xaml 编译为二进制 BAML，启动提速约 30%，编译期校验 Binding 路径
- **真实频谱接口**：PlayerEngine 内置 `SampleCaptureProvider`，在播放链中抓取解码后的 PCM 浮点样本（非 WASAPI 系统环回），可接入真实频谱分析；旧的 `Spectro.cs`（WASAPI 环回原型）已移入 `experiments/` 目录，不再参与编译
- **可插拔歌词匹配**：`ILyricsProvider` 接口定义歌词/封面搜索契约，酷狗与网易云各提供实现，任一接口失效不影响整体；请求带超时与降级策略；缓存 Key 改为归一化后的"歌手|标题"（MD5），避免同名歌曲因标签差异产生重复缓存，同时保留旧文件路径 Key 的向后兼容查找
- **文件关联 HKCU + 引导**：文件关联写入 `HKCU\Software\Classes`（无需管理员权限），`Assoc.IsAssociated()` 验证关联是否被系统 UserChoice 覆盖，不生效时通过 `Assoc.OpenDefaultAppsSettings()` 引导用户跳转 `ms-settings:defaultapps` 页面手动设置
- **安装器静默参数健壮性**：`/S /D=` 路径解析正确处理三种边界情况——引号包裹（`/D="C:\My App"`）、含空格未加引号（自动合并后续参数）、引号未闭合（合并直到闭合引号），并规范化路径尾部
- **零 GPL 依赖**：全部第三方库（NVorbis + NAudio）均为 MIT 许可证

## 功能

| 功能 | 说明 |
| --- | --- |
| 文件关联 | 安装后双击任意支持格式直接播放；同时加载其所在文件夹的全部音乐 |
| 单实例 | 已在播放时再次双击音乐，唤起现有窗口并切歌，不会开新进程 |
| 标签解析 | MP3/FLAC/M4A/OGG/OPUS/WAV 含完整标签与封面；`.aac`/`.wma` 裸流/ASF 无标签解析，标题取自文件名；智能识别下载器写入的占位标签（如 title=artist=album=kuwo），自动改用文件名信息 |
| 文件名推断 | 支持 `歌手 - 标题.mp3`、`歌手-标题.mp3`、`NN - 标题.mp3` 等常见命名，无标签文件也能正确显示 |
| 中文兼容 | 老歌 GBK 编码标签与 GBK 歌词文件自动识别，无乱码 |
| 封面 | 有内嵌封面直接显示；**无封面的歌曲按歌名自动生成专属渐变封面**（首字大字 + 歌曲信息，10 组极光配色），列表与播放页统一 |
| 播放页 | 点击封面进入全屏播放页：旋转黑胶（胶体颜色随歌曲配色变化、中央圆形封面、唱臂随播放/暂停搭下或抬起）+ 居中大字滚动歌词，`Esc` 或左上角返回 |
| 播放列表 | 底部列表按钮呼出抽屉：封面缩略图 + 标题/歌手/时长，单击切歌，当前播放高亮；**支持鼠标拖动排序、右键删除、移动动画**；搜索（标题/歌手/专辑/文件名）、五种排序、当前行均衡器动画 |
| 音量 | 点击底栏音量图标弹出竖直调节条，拖动或点击调节 |
| 歌词 | 同名 `.lrc` 自动加载（`歌曲名.lrc` 或 `歌曲名.mp3.lrc`），逐行滚动高亮，点击歌词行跳转；无歌词的歌曲显示优雅空状态 |
| 联网匹配 | 播放缺歌词/缺封面的歌曲时，后台自动联网匹配（可插拔 `ILyricsProvider` 架构：酷狗原版优先 → 网易云兜底，任一源失效自动降级，标题+歌手打分防翻唱误配），结果保存到 `%LOCALAPPDATA%\Aurora\Cache\`（归一化"歌手|标题" MD5 哈希命名，避免同名歌曲重复缓存），下次直接读取本地缓存；匹配失败提示"未找到"（本次会话内不重复请求）。底栏 🌐 按钮打开**联网匹配设置**：按音频格式（9 种）逐一开关联网匹配（默认全部启用）+ 一键清除歌词/封面缓存（显示缓存项数与体积，清除后本会话内可重新匹配） |
| 播放模式 | 列表循环 / 单曲循环 / 随机播放 |
| 记忆 | 记住上次打开的文件夹、音量、播放模式与主题，下次启动自动恢复 |
| 深浅主题 | 顶栏 🌙/☀️ 一键切换深色 / 浅色模式（歌词、列表、频谱全部联动） |
| 快捷键 | `空格` 播放暂停 · `←→` 快退快进 5 秒 · `↑↓` 音量 · `N/P` 下一首/上一首 · `M` 音量条 |
| 无边框圆角 UI | 极光渐变主题，Win11 系统级窗口圆角，可拖拽移动、边缘缩放、支持拖放导入文件/文件夹 |

> 关于"默认播放器"：Windows 10/11 保护用户对默认应用的选择。若系统已有其他播放器占用格式关联，安装后会以"选择打开方式"提示一次，选 **Aurora → 始终** 即可完成关联；此前未装过播放器的电脑上双击音乐将直接由 Aurora 打开。任何情况下 Aurora 都会出现在"打开方式"列表中。

## 使用

1. 运行 `AuroraPlayer-Setup.exe`，按向导完成安装（可改安装目录、勾选要关联的格式）
2. 双击任意音乐文件，或在播放器里点 **打开文件夹** / 直接把音乐文件（夹）拖进窗口
3. 卸载：`设置 → 应用 → Aurora 极光音乐`，或运行安装目录下的 `unins.exe`

`testmusic/` 内有多种格式的示例歌曲（含封面与歌词），可用于快速体验；不需要可整个删除。

## 构建

需要 Windows 10/11 + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。第三方依赖：NAudio 2.2.1 与 NVorbis 0.10.4 由 **NuGet 锁定版本**自动还原；Concentus 1.1.6 / Concentus.Oggfile 1.0.4 未上架 NuGet，随 `lib/` 目录提供（均为 MIT）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
# 或手动：
dotnet build AuroraPlayer.csproj -c Release
dotnet build unins.csproj -c Release
dotnet publish installer.csproj -c Release -r win-x64 --self-contained false
```

依次产出：

- `build\AuroraPlayer.exe` — 播放器（apphost，需同目录 DLL + .NET 10 运行时）
- `build\unins.exe` — 卸载器（需 .NET 10 运行时）
- `AuroraPlayer-Setup.exe` — 单文件安装包（PublishSingleFile，内嵌播放器/卸载器及全部 DLL，约 1.9 MB）

## 目录结构

```
MP3Player/
├── AuroraPlayer-Setup.exe   ★ 成品安装包（单文件，约 1.9 MB）
├── AuroraPlayer.csproj       主程序项目（WPF, net10.0-windows）
├── unins.csproj              卸载器项目（WinForms, net10.0-windows）
├── installer.csproj          安装器项目（WinForms, net10.0-windows, PublishSingleFile）
├── build.ps1                 一键构建脚本（PowerShell）
├── build/                    编译输出（免安装版在此）
├── lib/                      第三方依赖 DLL（NuGet 未收录的部分）
│   ├── Concentus.dll         OPUS 纯托管解码器（NuGet 无此包，本地引用）
│   └── Concentus.Oggfile.dll OPUS Ogg 解封装（NuGet 无此包，本地引用）
│   （NAudio 2.2.1 / NVorbis 0.10.4 已迁移 NuGet PackageReference 锁定版本）
├── tests/                    单元测试（xunit，dotnet test 运行，39 用例）
│   ├── Aurora.Tests.csproj
│   ├── PlaybackControllerTests.cs
│   ├── PlaylistManagerTests.cs
│   ├── NetMatchSettingsTests.cs  按格式联网匹配开关（Settings 键逻辑）
│   └── DurationTests.cs      Mp3Duration / AacDuration（合成帧流）
├── assets/                   图标素材（app.ico / icon256.png）
├── experiments/              实验性代码（不参与编译）
│   └── Spectro.cs            WASAPI 环回采集 + FFT 原型（已被 PlayerEngine.SampleCaptureProvider 取代）
├── src/
│   ├── App.cs                入口：单实例互斥、命名管道转发、关联标记
│   ├── MainWindow.cs         主窗口（UI 渲染 + UI 交互逻辑 code-behind：拖拽排序/快捷键/拖放/窗口控制；核心业务逻辑已下沉到 ViewModel）
│   ├── ui.xaml               界面（Page 编译为 BAML，Binding 绑定 ViewModel）
│   ├── ViewModelBase.cs      MVVM 基类（INotifyPropertyChanged + SetProperty）
│   ├── RelayCommand.cs       ICommand 实现
│   ├── MainViewModel.cs      主视图模型（协调器，统筹 PlayerEngine/PlaybackController/PlaylistManager，UI 线程安全调度）
│   ├── PlaybackController.cs 播放模式状态机（列表循环/单曲循环/随机，纯逻辑可单元测试）
│   ├── PlaylistManager.cs    播放列表管理（增删/去重/排序/搜索/拖动排序）
│   ├── PlayerEngine.cs       播放引擎（NAudio WaveOutEvent + SampleCaptureProvider 真实频谱样本抓取）
│   ├── VorbisWaveReader.cs   OGG/OPUS 流式解码器（WaveStream + ISampleProvider，NVorbis 直接接入播放链，无临时 WAV）
│   ├── TagReaderService.cs   统一标签读取服务（9 种格式封装 + 文件名推断 + 垃圾标签清除）
│   ├── ILyricsProvider.cs    歌词/封面提供者接口（可插拔架构，含归一化缓存 Key 工具）
│   ├── Model.cs              轨道模型、目录扫描、设置持久化（SettingsStore）、缓存目录读取
│   ├── Id3.cs                ID3v1/v2 标签与封面解析（UTF-8→GBK 宽松解码）
│   ├── Tags.cs               FLAC / M4A / OGG / WAV / WMA 标签、封面与时长解析
│   ├── Lrc.cs                LRC 歌词解析（多时间标签/offset/元数据）
│   ├── NetMatch.cs           联网匹配编排层（数据源注册表 + 缓存管理/清除 + 按格式开关 + 降级链，TLS1.2）
│   ├── CoverArt.cs           自动生成渐变封面
│   ├── Assoc.cs              音频格式关联（HKCU，无需管理员，9 种格式，含 IsAssociated 验证 + OpenDefaultAppsSettings 引导）
│   ├── Installer.cs          安装向导（单文件发布，内嵌播放器/卸载器，3×3格式选择，/S /D= 路径含空格引号健壮处理）
│   └── Uninstaller.cs        卸载器（两阶段自删除，Environment.ProcessPath 兼容单文件部署）
└── testmusic/                示例音乐（可删）
```

## 已知限制

- `.aac`（ADTS 裸流）与 `.wma` 无容器标签解析：标题/歌手取自文件名推断，封面不显示；`.aac` 时长为 ADTS 帧头 CBR 估算（VBR 文件可能有偏差），`.wma` 时长在首次播放后显示
- 频谱 UI 当前未启用；`PlayerEngine` 已内置 `SampleCaptureProvider` 可输出当前歌曲的 PCM 浮点样本（非系统环回），如需频谱 UI 可直接接入
- 随机播放的"上一首"仅在本轮会话内有效
- OGG/OPUS 采用 NVorbis 纯托管流式解码（`VorbisWaveReader`），直接接入 NAudio 播放链，无临时文件、无大小上限；超大文件（如数小时的 OGG）首次加载时会有短暂索引构建延迟
- 目标框架 .NET 10 (net10.0-windows)，采用框架依赖部署，需目标系统安装 .NET 10 Desktop Runtime（详见"成品"章节说明）；安装器与卸载器同样基于 .NET 10，单文件发布
