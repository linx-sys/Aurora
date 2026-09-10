# Aurora 媒体库数据库（DATABASE）

> 实现：`src/LibraryDatabase.cs`（ILibraryStore 唯一实现）、`src/Model.cs`（Library 扫描/物化）、
> `src/LibraryImportController.cs`（两段式启动同步）。

## 基本信息

- 引擎：Microsoft.Data.Sqlite（MIT），**WAL** 模式（后台扫描写 + UI 读不互斥）
- 路径：`%APPDATA%\AuroraPlayer\library.db`
- 定位（P1-3 起）：**库数据持久化权威层**——启动秒开 + 差分同步；运行期内存 PlaylistManager 仍是唯一可变工作集（单一数据源原则不变）

## 表结构（tracks）

| 列 | 类型 | 说明 |
| --- | --- | --- |
| path | TEXT PK | 文件全路径 |
| file_name | TEXT | 文件名 |
| title / artist / album | TEXT | 标签（可空） |
| duration | REAL | 秒（0=未知） |
| bytes / last_modified | INTEGER | **指纹**：大小 + LastWriteTimeUtc.Ticks |
| cover | BLOB | 标签内嵌封面 |
| track_gain / track_peak | REAL | ReplayGain 2.0 结果（旧库自动补列迁移） |

> 扩展表（artists/albums/playlists/history/favorites）与扩展列（Genre/Bitrate/SampleRate/Hash）
> 为 3.0 候选项，暂不实施——决策依据见 docs/ROADMAP.md 阶段 5。

## 读写 API（ILibraryStore）

`TryGet(path)` · `GetByPrefix(dir)` · `Upsert(row)` · `UpsertMany(rows)`（单事务）· `Delete(paths)` · `DeleteMissingUnder(dir, present)`（**目录边界前缀**匹配：补全分隔符，`D:\Music` 不误伤 `D:\MusicBackup`）

## 两段式启动同步（LibraryImportController.LoadDirectory）

```
① DB 秒开：GetByPrefix(lastDir) → Library.BuildTracksFromRows（O(n) 物化；
   每行同名 .lrc 探测 + 外部封面兜底现读；已消失文件跳过）→ ReplaceTracks 填充列表
② 差分同步（权威）：EnumerateFiles → BuildTracksIncremental
   （指纹命中→复用；变化→重解析并 UpsertMany；消失→DeleteMissingUnder 清理）
   → ReplaceTracks 整表替换 + 播放状态恢复（lastDir/lastTrack）
```

## 缓存规避规则（重要）

只缓存"贵"的数据（标签/时长/内嵌封面/ReplayGain）。**lrc 文本与外部封面兜底每次现读**——
它们会在缓存写入后变化（联网匹配后到），缓存会吃掉更新。

## 测试覆盖

`tests/LibraryDatabaseTests.cs`（CRUD/批量/前缀/清理）+ `tests/LibraryDbFirstTests.cs`
（秒开物化/消失跳过/lrc 配对/差分新增变更/边界清理）。
