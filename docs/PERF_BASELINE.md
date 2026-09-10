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
| DB 批量写入 UpsertMany | 10,000 行 | 320 ms | 单事务 |
| DB 前缀读 GetByPrefix | 10,000 行 | 39 ms | 索引范围扫描 + 行构造 |
| **DB 秒开物化**（GetByPrefix + BuildTracksFromRows） | 1,000 首 | **1,411 ms** | 含每文件 3~4 次 File.Exists 探测 |
| **差分同步**（BuildTracksIncremental，热缓存全命中） | 1,000 首 | **1,144 ms** | 指纹全命中，跳过标签解析 |

外推（线性，仅量级参考）：10,000 首秒开 ≈ 14s；100,000 首 ≈ 140s。
**结论：当前物化路径在超大库下不满足"10 万曲启动 <2s"目标。**

## 已定位热点（BuildTracksFromRows，按行探测）

每首曲目物化时做 3~4 次逐文件文件系统探测：
1. `File.Exists(row.Path)`（存在性）
2. 同名 `.lrc` 探测 ×2（`xxx.lrc` / `xxx.mp3.lrc`）
3. 外部封面兜底探测（`ApplyExternalCoverFallback` 内）

每次探测 ≈0.3~0.4ms（机械盘/杀软过滤驱动下更贵）。

**优化方案（未实施，下一步候选）**：
- 用一次目录枚举替代逐文件探测：对 `lastDir` 做一趟 `EnumerateFiles`（或按目录 `Directory.EnumerateFiles(dir, "*.lrc")`），建立 lrcMap / 封面候选表后再批量物化 —— 每目录 1 次枚举替代每文件 3~4 次探测，预估 10k 首进入 <2s 区间
- 存在性探测可与差分同步的枚举结果共享（秒开与同步本就先后执行）
- 秒开列表可先只做"标题/歌手/时长"轻物化（零探测），lrc/封面随权威同步补齐（有 UI 短暂补齐过程，需权衡）

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
