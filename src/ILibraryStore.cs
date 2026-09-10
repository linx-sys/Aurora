/* ============================================================
 * ILibraryStore.cs — 音乐库正式核心存储接口（P1-3）
 * SQLite（LibraryDatabase）为库数据的持久化权威层：
 * 启动时 DB 优先秒开列表，后台文件系统扫描做指纹差分同步。
 * 运行期内存 PlaylistManager 仍是唯一可变工作集（单一数据源不变）。
 * ============================================================ */
using System;
using System.Collections.Generic;

namespace Aurora
{
    public interface ILibraryStore
    {
        /// <summary>按路径取行；不存在返回 null。</summary>
        TrackRow? TryGet(string path);

        /// <summary>取目录前缀下的所有行（DB 优先启动 / 增量清理用）。</summary>
        List<TrackRow> GetByPrefix(string dirPrefix);

        /// <summary>写入或更新一行（按路径主键）。</summary>
        void Upsert(TrackRow row);

        /// <summary>批量写入（单事务）。</summary>
        void UpsertMany(IEnumerable<TrackRow> rows);

        /// <summary>删除指定路径集合中的行。</summary>
        void Delete(IEnumerable<string> paths);

        /// <summary>
        /// 删除 dirPrefix 目录下不在 present 集合中的过期行。
        /// 前缀按"目录边界"匹配（补全分隔符），避免 "D:\Music" 误伤 "D:\MusicBackup"。
        /// 返回删除的行数。
        /// </summary>
        int DeleteMissingUnder(string dirPrefix, HashSet<string> presentPaths);
    }
}
