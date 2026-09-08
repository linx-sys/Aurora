/* ============================================================
 * NetMatch.cs — 联网匹配歌词/封面（后台线程，不阻塞播放）
 * 编排层：缓存管理（统计/清除）+ 按格式启用开关 + 数据源注册表 + 降级链。
 * 数据源注册表 Providers = { 酷狗, 网易云 }，按序尝试，
 * 第一个命中的源生效；单源异常自动降级到下一个。
 * 新增数据源：在 ILyricsProvider.cs 实现接口后加入 Providers。
 * 保存位置：%LOCALAPPDATA%\Aurora\Cache\，Key 为归一化"歌手|标题"
 * 的 MD5；旧版"文件路径" Key 保留向后兼容查找。
 * 依赖：BCL 自带 HttpWebRequest + System.Text.Json（见 ILyricsProvider.cs）
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Aurora
{
    public class NetMatchResult
    {
        public bool LyricsMatched;
        public bool CoverMatched;
        public bool NotFound;          // 所有数据源都没找到（区别于网络异常）
        public string LyricPath = "";
        public string CoverPath = "";
        public string Source = "";
        public string Message = "";
    }

    public static class NetMatch
    {
        /// <summary>
        /// 数据源注册表（可插拔）：按序尝试，第一个命中的源生效。
        /// </summary>
        public static readonly List<ILyricsProvider> Providers = new List<ILyricsProvider>
        {
            new KugouProvider(),
            new NeteaseProvider(),
        };

        /* ============================================================
         * 按格式的联网匹配开关 / 缓存管理
         * ============================================================ */

        const string ExtEnabledPrefix = "netmatch.ext.";   // settings.ini 键前缀，值 "1"/"0"，默认启用

        /// <summary>某扩展名对应的设置键（如 netmatch.ext.mp3）。</summary>
        public static string SettingsKeyForExt(string ext)
        {
            return ExtEnabledPrefix + (ext ?? "").TrimStart('.').ToLowerInvariant();
        }

        /// <summary>纯逻辑：给定扩展名与设置查询函数判断是否启用（便于单元测试）。</summary>
        public static bool IsEnabledForExt(string ext, Func<string, string> lookup)
        {
            if (string.IsNullOrEmpty(ext)) return true;
            string v = lookup(SettingsKeyForExt(ext));
            return v != "0";   // 缺省/任意非 "0" 值均视为启用（保守默认）
        }

        /// <summary>某音频文件是否启用联网匹配（按扩展名读设置，默认全部启用）。</summary>
        public static bool EnabledFor(string musicPath)
        {
            return IsEnabledForExt(Path.GetExtension(musicPath ?? ""), k => Settings.Get(k, "1"));
        }

        /// <summary>统计歌词/封面缓存：返回（文件数，总字节）。</summary>
        public static (int files, long bytes) CacheStats()
        {
            int files = 0;
            long bytes = 0;
            try
            {
                foreach (string dir in CacheDirs())
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try { files++; bytes += new FileInfo(f).Length; } catch { }
                    }
                }
            }
            catch { }
            return (files, bytes);
        }

        /// <summary>清空歌词/封面缓存。返回删除的（文件数，总字节）。</summary>
        public static (int files, long bytes) ClearCache()
        {
            int files = 0;
            long bytes = 0;
            try
            {
                foreach (string dir in CacheDirs())
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            bytes += fi.Length;
                            fi.Delete();
                            files++;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return (files, bytes);
        }

        static IEnumerable<string> CacheDirs()
        {
            yield return Path.Combine(CacheRoot, "Lyrics");
            yield return Path.Combine(CacheRoot, "Covers");
        }

        /* ===== 缓存目录（歌词/封面统一存放，不污染音频目录） ===== */

        static string CacheRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora", "Cache"); }
        }

        static string GetCacheKey(string musicPath)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(musicPath.ToLowerInvariant()));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>归一化缓存 Key：歌手|标题（MD5），避免同名歌曲因标签差异产生重复缓存。</summary>
        static string GetCacheKeyByMetadata(string title, string artist)
        {
            string t = (title ?? "").Trim().ToLowerInvariant();
            string a = (artist ?? "").Trim().ToLowerInvariant();
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(a + "|" + t));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string GetLyricCachePath(string musicPath) { return Path.Combine(CacheRoot, "Lyrics", GetCacheKey(musicPath) + ".lrc"); }
        public static string GetCoverCachePath(string musicPath) { return Path.Combine(CacheRoot, "Covers", GetCacheKey(musicPath) + ".jpg"); }

        /// <summary>基于元数据的缓存路径（新写入使用此路径，归一化 Key）。</summary>
        static string GetLyricCachePathByMeta(string title, string artist) { return Path.Combine(CacheRoot, "Lyrics", GetCacheKeyByMetadata(title, artist) + ".lrc"); }
        static string GetCoverCachePathByMeta(string title, string artist) { return Path.Combine(CacheRoot, "Covers", GetCacheKeyByMetadata(title, artist) + ".jpg"); }

        /* ============================================================
         * 对外入口
         * ============================================================ */

        /// <summary>查找歌词文件：先查缓存目录，再查同目录（向后兼容旧版）。</summary>
        public static string FindLyricFile(string musicPath)
        {
            try
            {
                string cached = GetLyricCachePath(musicPath);
                if (File.Exists(cached)) return cached;
                string a = Path.ChangeExtension(musicPath, ".lrc");
                if (File.Exists(a)) return a;
                string b = musicPath + ".lrc";
                if (File.Exists(b)) return b;
            }
            catch { }
            return null;
        }

        /// <summary>封面是否已存在（缓存目录优先，同目录向后兼容）。</summary>
        public static bool HasCover(string musicPath)
        {
            try
            {
                string cached = GetCoverCachePath(musicPath);
                if (File.Exists(cached) && new FileInfo(cached).Length > 0) return true;
                string p = Path.ChangeExtension(musicPath, ".jpg");
                return File.Exists(p) && new FileInfo(p).Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 后台线程开始匹配；完成后回调 onDone（线程池线程，调用方需自行调度到 UI 线程）。
        /// 单曲全流程：按 Providers 顺序逐源尝试，第一个有结果的源生效。
        /// </summary>
        public static void Start(string musicPath, string title, string artist, Action<NetMatchResult> onDone)
        {
            ThreadPool.QueueUserWorkItem(_ => RunCore(musicPath, title, artist, onDone));
        }

        static void RunCore(string musicPath, string title, string artist, Action<NetMatchResult> onDone)
        {
            // 新缓存：基于归一化"歌手|标题"的 Key（避免同名歌曲重复缓存）
            // 旧缓存：基于文件路径的 Key（向后兼容，仅用于查找）
            string newLyricPath = GetLyricCachePathByMeta(title, artist);
            string newCoverPath = GetCoverCachePathByMeta(title, artist);
            string oldLyricPath = GetLyricCachePath(musicPath);
            string oldCoverPath = GetCoverCachePath(musicPath);

            var r = new NetMatchResult
            {
                LyricPath = File.Exists(newLyricPath) ? newLyricPath : (File.Exists(oldLyricPath) ? oldLyricPath : newLyricPath),
                CoverPath = File.Exists(newCoverPath) ? newCoverPath : (File.Exists(oldCoverPath) ? oldCoverPath : newCoverPath),
            };
            try
            {
                // 缓存命中则跳过网络请求
                bool lyricCached = File.Exists(r.LyricPath) && new FileInfo(r.LyricPath).Length > 0;
                bool coverCached = File.Exists(r.CoverPath) && new FileInfo(r.CoverPath).Length > 0;
                if (lyricCached) r.LyricsMatched = true;
                if (coverCached) r.CoverMatched = true;

                if (!lyricCached || !coverCached)
                {
                    string lastError = null;
                    foreach (ILyricsProvider provider in Providers)
                    {
                        try
                        {
                            if (provider.Match(r, musicPath, title, artist))
                            {
                                r.Source = provider.Name;   // 命中源生效，不再尝试后续源
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            // 单源失效（网络异常/接口变更）自动降级到下一个
                            MainViewModel.Dbg("NetMatch provider " + provider.Name + " FAIL: " + ex.Message);
                            lastError = ex.Message;
                        }
                    }

                    if (!r.LyricsMatched && !r.CoverMatched)
                    {
                        // 全部源干净返回但无结果 → NotFound（"未找到"）；
                        // 有源抛异常 → 不置 NotFound（UI 按"联网匹配失败"提示）
                        r.NotFound = lastError == null;
                        r.Message = lastError ?? "未找到";
                    }
                }
            }
            catch (Exception ex)
            {
                r.Message = ex.Message;
            }
            finally
            {
                if (onDone != null)
                    try { onDone(r); } catch { }
            }
        }
    }
}
