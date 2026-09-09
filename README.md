# Aurora · 极光音乐

**一个漂亮、轻量、本地优先、打开即用的 Windows 本地音乐播放器。**

基于 .NET 10 + WPF（MVVM 分层），NAudio 音频管线，全 MIT 许可证第三方依赖。无广告、无遥测、无需登录。

<!-- TODO：截图（播放页 黑胶+歌词 / 列表抽屉 / 浅色主题），1~3 张 GIF 最佳 -->

## 特性亮点

✓ **极简 UI** — 极光渐变主题，深/浅色一键切换，Win11 圆角无边框窗口
✓ **本地优先** — 不强制联网；歌词/封面缺失时才自动联网匹配（可关）
✓ **9 种格式** — MP3 / FLAC / M4A / AAC / WAV / WMA / OGG / OGA / OPUS
✓ **黑胶播放页** — 旋转黑胶 + 唱臂动画 + 居中大字歌词，胶体颜色随歌曲变化
✓ **ReplayGain 2.0** — EBU R128 响度均衡，歌与歌之间不再忽大忽小（可关）
✓ **无缝衔接** — 常驻混音器管线 + 下一首预加载；可选 2 秒跨淡入淡出
✓ **WASAPI 独占** — 发烧友可选的独占输出（不可用时自动回退）
✓ **大曲库友好** — SQLite 媒体库缓存 + 增量扫描，二次扫描不再重复解析标签
✓ **中文兼容** — 老歌 GBK 标签/歌词自动识别，无乱码
✓ **智能匹配** — 酷狗 → 网易云双源降级，标题+歌手打分防翻唱误配
✓ **单实例** — 双击新歌唤起现有窗口，绝不开第二个进程

## 下载

| 产物 | 适用 | 说明 |
| --- | --- | --- |
| **AuroraPlayer-Setup.exe** | 推荐大多数用户 | 单文件安装包（约 4 MB）。可选安装目录、格式关联、自带卸载器。**需 .NET 10 Desktop Runtime** |
| **Aurora-x64-Portable.exe** | 免安装 / U 盘党 | 自包含单文件（内嵌 .NET 10 运行时），下载即双击，不写注册表 |
| **Aurora-win64.zip** | 便携目录 | 解压即用的完整文件版（同样需 .NET 10 Desktop Runtime） |

> 从 [Releases](https://github.com/linx-sys/Aurora/releases) 下载。框架依赖版本需要 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（Win10 1607+ / Win11）；未安装时启动会提示"找不到托管 DLL"。
>
> 应用内置**更新检查**（每 24h 最多一次，可在设置中关闭）：发现新版本会提示并打开发布页。

## 快速开始

1. 安装（或直接运行 Portable 版）
2. 首次启动选择音乐文件夹；之后也可点 **打开文件夹** / 直接拖放文件（夹）进窗口
3. 双击任意音乐文件即可关联播放（安装时可一键关联全部 9 种格式）

卸载：`设置 → 应用 → Aurora 极光音乐`，音乐文件不会被删除。

## 支持格式

| 格式 | 解码 | 标签/封面 |
| --- | --- | --- |
| `.mp3` | Media Foundation | ID3v1/v2.2/v2.3/v2.4 + 封面 |
| `.m4a` | Media Foundation | iTunes ilst + 封面 |
| `.flac` | Media Foundation | VORBIS_COMMENT + 内嵌封面 |
| `.wav` | Media Foundation | LIST INFO / id3 chunk |
| `.aac` | Media Foundation | 裸流无标签：文件名推断 + ADTS 时长估算 |
| `.wma` | Media Foundation | 无标签解析：文件名推断 |
| `.ogg` / `.oga` | **NVorbis 纯托管** | VorbisComment |
| `.opus` | **Concentus 纯托管** | VorbisComment |

> OGG/OPUS 纯托管流式解码直接接入 NAudio 播放链，无临时 WAV、无文件大小上限。详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 功能一览

| 功能 | 说明 |
| --- | --- |
| 播放列表 | 封面缩略图抽屉、拖动排序、搜索（标题/歌手/专辑/文件名）、5 种排序、删除动画 |
| 歌词 | 同名 `.lrc` 自动加载、逐行高亮滚动、点击行跳转；元数据分组展示 |
| 联网匹配 | 按格式开关、缓存统计与一键清除（`%LOCALAPPDATA%\Aurora\Cache\`） |
| 播放模式 | 列表循环 / 单曲循环 / 随机（随机维护播放轨迹：上一首真回退、下一首可前进） |
| 封面 | 内嵌封面优先；无封面按歌名生成专属渐变封面（10 组极光配色） |
| 记忆 | 上次文件夹、音量、播放模式、主题、上次播放曲目，重启自动恢复 |
| 快捷键 | `空格` 播放暂停 · `←→` 快进快退 5 秒 · `↑↓` 音量 · `N/P` 切歌 · `M` 静音 |
| 音频管线 | 常驻混音器（48kHz float），多声道截断 / 单声道转立体声 / 重采样自动归一 |

## 诊断

日志位于 `%LOCALAPPDATA%\Aurora\Logs\`：

- `aurora.log` — 运行日志（超 5MB 自动轮转）
- `crash_*.log` — 崩溃日志（一次崩溃一个文件，含版本/系统/完整堆栈）

反馈问题时请附上对应日志。

## 构建

需要 Windows 10/11 + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1    # 4 步：主程序 → 卸载器 → 安装器 → Portable
dotnet test tests/Aurora.Tests.csproj                 # 173 个单元测试
```

依赖：NAudio / NVorbis / Microsoft.Data.Sqlite 由 NuGet 锁定版本还原；Concentus（NuGet 未上架）随 `lib/` 提供，均为 MIT。CI（GitHub Actions）自动执行构建 + 测试，打 `v*` tag 自动发布 Release。

## 已知限制

- `.aac`（ADTS）/ `.wma` 无容器标签：标题取自文件名，封面不显示；`.aac` 时长为 CBR 估算
- 频谱 UI 未启用（管线已内置 PCM 样本抓取，接入即可）
- 随机播放"上一首"仅本会话有效
- ReplayGain 为懒分析：每首歌首次播放时后台分析，完成后的播放才应用增益
- 更新检查仅提示 + 打开发布页，不自动替换程序文件

## 文档

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — 架构分层、管线设计、关键决策、代码结构

## 许可

代码与第三方依赖均为 MIT。
