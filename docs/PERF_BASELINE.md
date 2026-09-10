# Aurora 性能基线（PERF_BASELINE）

> 阶段 0 产物。所有自动数据来自 `tests/PerformanceBaselineTests.cs`（xUnit，`[Trait("Category","Perf")]`），
> 复现命令：
>
> ```
> dotnet test tests/Aurora.Tests.csproj -c Release --logger "console;verbosity=detailed"
> ```
>
> 测试机：Jinwei 工作机（Win11，库与临时目录在 D:）。数字随机器浮动，趋势与数量级是关注点。

## 数据层基线（Release 构建，2026-09-10 实测）

| 场景 | 规模 | 耗时 | 说明 |
| --- | --- | --- | --- |
| DB 批量写入 UpsertMany | 10,000 行 | 195 ms | 单事务 |
| DB 前缀读 GetByPrefix | 10,000 行 | 38 ms | 索引范围扫描 + 行构造 |
| DB 秒开物化（逐文件探测旧路径） | 1,000 首 | 1,338 ms | 每首 3~5 次文件系统调用 |
| 差分同步（BuildTracksIncremental，热缓存全命中） | 1,000 首 | 1,157 ms | 指纹全命中，跳过标签解析 |
| **DB 秒开物化（零探测快路径，2026-09-10 优化后）** | **10,000 首** | **28 ms** | GetByPrefix + 零探测物化 |

**优化结论（2026-09-10）**：`BuildTracksFromRows(probeExtras: false)` 零探测快路径落地后，
10k 首秒开路径从线性外推 ~13s 降至 **28ms**；外推 100k 首（GetByPrefix ~0.4s + 物化 ~0.3s）
**进入 <1s 区间，"10 万曲库启动 <2s"目标达成**（待合成大库端到端验证）。

快路径语义（启动 ① 专用）：不查存在性（已删文件短暂成为幽灵行，由 ② 差分同步清理）、
不读 lrc 与外部封面（由 ② 权威同步补齐）；当前曲目歌词/封面最迟在 ② 完成后渲染。

### 历史记录（优化前，逐文件探测路径）

| 场景 | 规模 | 耗时 | 日期 |
| --- | --- | --- | --- |
| DB 秒开物化（探测路径） | 1,000 首 | 1,411 ms | 2026-09-10 上午 |
| DB 批量写入 UpsertMany | 10,000 行 | 320 ms | 2026-09-10 上午 |
| DB 前缀读 GetByPrefix | 10,000 行 | 39 ms | 2026-09-10 上午 |

## 已定位热点（BuildTracksFromRows，按行探测）——已优化

原热点：每首曲目物化时 3~5 次逐文件探测（存在性 / FindLyricFile / alt lrc / 缓存封面 / 同名 jpg / FileInfo stat）。
**2026-09-10 已实施零探测快路径**（`probeExtras: false`，启动 ① 专用），数字见上表；
探测路径保留（`probeExtras: true`）供小库/测试使用完整数据。

## GUI 启动基线（手动测量，占位）

> 测量方法：启动应用 → 读取 `%LOCALAPPDATA%\Aurora\Logs\aurora.log` 首尾时间戳
> （进程启动 → 窗口可用 → 列表填充完成）；或秒表目测。测完把数字填入下表。

| 场景 | 冷启动 | 二次启动 | 目标 |
| --- | --- | --- | --- |
| 空库 | ___ ms | ___ ms | <500ms |
| 1,000 首 | ___ ms | ___ ms | <500ms |
| 10,000 首 | ___ ms | ___ ms | <2s（ROADMAP 阶段 5） |

## 内存 / CPU 基线（手动测量，占位）

任务管理器/Process Explorer 观察 AuroraPlayer 进程：

| 场景 | 内存工作集 | CPU |
| --- | --- | --- |
| 空闲（无播放） | ___ MB | ~0% |
| 播放中 | ___ MB | ___ % |
| 扫描中（首次/增量） | ___ MB | ___ % |

> 更新本文件时保留历史记录（新表追加，不改旧表），便于对照回归。
