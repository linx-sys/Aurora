#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * ILyricsProvider.cs — 可插拔歌词/封面提供者架构
 * 接口 + 共享基础设施（HTTP/JSON/缓存写入）+ 酷狗、网易云两个实现。
 * 新增数据源：实现 ILyricsProvider，加入 NetMatch.Providers 即生效，
 * 失效自动降级到下一个源。
 * 依赖：BCL 自带 HttpWebRequest + System.Text.Json
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Aurora
{
    /// <summary>
    /// 歌词/封面提供者接口。实现方在后台线程池线程上被调用，可阻塞（HTTP）。
    /// </summary>
    public interface ILyricsProvider
    {
        /// <summary>数据源显示名（用于 Toast 提示与 NetMatchResult.Source）。</summary>
        string Name { get; }

        /// <summary>
        /// 尝试匹配歌词与封面。只应补全尚未命中的部分
        /// （r.LyricsMatched / r.CoverMatched 已为 true 的项不再覆盖）。
        /// 返回 true 表示本源至少命中一项。
        /// </summary>
        bool Match(NetMatchResult r, string musicPath, string title, string artist);
    }

    /// <summary>
    /// 提供者基类：封装 HTTP、JSON、打分辅助、缓存写入等共享设施。
    /// 请求 15s 超时；两次请求间隔 ≥600ms 防频控。
    /// </summary>
    public abstract class LyricsProviderBase : ILyricsProvider
    {
        protected const int HttpTimeoutMs = 15000;
        protected const int RateLimitMs = 600;          // 两次请求间隔，避免触发数据源频控
        protected const int MinAcceptableScore = 50;    // 打分低于此值视为未找到
        protected const int SearchLimit = 10;
        protected const int MaxLyricsCandidates = 3;

        protected const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

        static LyricsProviderBase()
        {
            // 部分数据源（如 imge.kugou.com）与默认 TLS 不兼容，
            // 显式启用 TLS 1.2，否则 HTTPS 报"未能创建 SSL/TLS 安全通道"。
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        public abstract string Name { get; }
        public abstract bool Match(NetMatchResult r, string musicPath, string title, string artist);

        /* ============================================================
         * HTTP 基础（HttpWebRequest）
         * ============================================================ */

        protected static HttpWebRequest NewRequest(string url, string referer)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = HttpTimeoutMs;
            req.ReadWriteTimeout = HttpTimeoutMs;
            req.UserAgent = Ua;
            req.Accept = "application/json,text/plain,*/*";
            if (referer != null) req.Referer = referer;
            return req;
        }

        protected static string HttpGet(string url, string referer)
        {
            HttpWebRequest req = NewRequest(url, referer);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        protected static byte[] HttpGetBytes(string url, string referer)
        {
            HttpWebRequest req = NewRequest(url, referer);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[16384];
                int n;
                while ((n = resp.GetResponseStream().Read(buf, 0, buf.Length)) > 0)
                    ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        protected static void SleepRate() { Thread.Sleep(RateLimitMs); }

        /* ============================================================
         * JSON 辅助（System.Text.Json → Dictionary/object[] 树）
         * ============================================================ */

        protected static Dictionary<string, object> JObj(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return JsonElementToObject(doc.RootElement) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        /// <summary>将 JsonElement 递归转换为通用 object 树。</summary>
        static object JsonElementToObject(System.Text.Json.JsonElement el)
        {
            switch (el.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in el.EnumerateObject())
                        dict[prop.Name] = JsonElementToObject(prop.Value);
                    return dict;
                case System.Text.Json.JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in el.EnumerateArray())
                        list.Add(JsonElementToObject(item));
                    return list.ToArray();
                case System.Text.Json.JsonValueKind.String:
                    return el.GetString();
                case System.Text.Json.JsonValueKind.Number:
                    if (el.TryGetInt64(out long l)) return l;
                    if (el.TryGetDouble(out double d)) return d;
                    return el.GetRawText();
                case System.Text.Json.JsonValueKind.True: return true;
                case System.Text.Json.JsonValueKind.False: return false;
                case System.Text.Json.JsonValueKind.Null:
                default:
                    return null;
            }
        }

        protected static object JGet(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        protected static object[] JArr(object o) { return o as object[]; }

        protected static string JStr(object o)
        {
            return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        protected static long JLong(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        protected static int JInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        /* ============================================================
         * 通用：打分 / 归一化 / 保存
         * ============================================================ */

        /// <summary>归一化：小写、去空白与标点、去括号注释（如 (Live)、[深情版]）。</summary>
        protected static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>写入歌词缓存（UTF-8 无 BOM）；已命中则跳过。失败抛异常由编排层记录。</summary>
        protected static void SaveLyrics(NetMatchResult r, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || r.LyricsMatched) return;
            Directory.CreateDirectory(Path.GetDirectoryName(r.LyricPath));
            File.WriteAllText(r.LyricPath, text, new UTF8Encoding(false));
            r.LyricsMatched = true;
        }

        /// <summary>下载封面到缓存；网络/写盘失败记录日志后静默（封面属锦上添花）。</summary>
        protected static void SaveCover(NetMatchResult r, string coverUrl)
        {
            if (string.IsNullOrEmpty(coverUrl) || r.CoverMatched) return;
            try
            {
                byte[] bytes = HttpGetBytes(coverUrl, null);
                if (bytes.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(r.CoverPath));
                    File.WriteAllBytes(r.CoverPath, bytes);
                    r.CoverMatched = true;
                }
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("SaveCover FAIL: " + coverUrl + " -> " + ex.Message);
            }
        }
    }

    /* ============================================================
     * 数据源一：酷狗（默认，原版优先，中文覆盖最好）
     * 搜索 songsearch.kugou.com → 歌词 krcs.kugou.com（Base64 LRC）
     * → 封面 imge.kugou.com（Image 模板或 AlbumID）
     * ============================================================ */

    public sealed class KugouProvider : LyricsProviderBase
    {
        public override string Name { get { return "酷狗"; } }

        public override bool Match(NetMatchResult r, string musicPath, string title, string artist)
        {
            string query = string.IsNullOrEmpty(artist) ? title : title + " " + artist;
            string url = "https://songsearch.kugou.com/song_search_v2?keyword=" +
                         Uri.EscapeDataString(query) + "&page=1&pagesize=" + SearchLimit +
                         "&platform=WebFilter&tag=em&filter=2&iscorrection=1";

            Dictionary<string, object> root = JObj(HttpGet(url, null));
            object data = root != null ? JGet(root, "data") : null;
            object[] lists = data != null ? JArr(JGet(data, "lists")) : null;
            if (lists == null || lists.Length == 0) return false;

            // 打分选最优：标题一致 +100；歌手一致 +40（部分匹配 +20）；原版标记 +20
            object best = null;
            string bestHash = "", bestName = "", bestSinger = "", bestAlbumId = "", bestImage = "";
            int bestScore = -1;
            for (int i = 0; i < lists.Length; i++)
            {
                object item = lists[i];
                string hash = JStr(JGet(item, "FileHash"));
                if (hash.Length == 0) continue;
                string name = StripEm(JStr(JGet(item, "SongName")));
                string singer = StripEm(JStr(JGet(item, "SingerName")));
                string albumId = JStr(JGet(item, "AlbumID"));
                string image = JStr(JGet(item, "Image"));
                int isOriginal = JInt(JGet(item, "IsOriginal"));

                int score = Score(name, singer, title, artist, isOriginal);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                    bestHash = hash; bestName = name; bestSinger = singer;
                    bestAlbumId = albumId; bestImage = image;
                }
            }
            if (best == null || bestScore < MinAcceptableScore) return false;

            // 歌词：候选列表 → 下载（Base64 → UTF-8 LRC）
            SleepRate();
            string kw = string.IsNullOrEmpty(artist) ? bestName : bestName + " " + artist;
            string lyricSearch = "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi" +
                                 "&keyword=" + Uri.EscapeDataString(kw) + "&hash=" + bestHash;
            Dictionary<string, object> lroot = JObj(HttpGet(lyricSearch, null));
            object[] cands = lroot != null ? JArr(JGet(lroot, "candidates")) : null;
            if (cands != null && cands.Length > 0)
            {
                // 优先歌手匹配的候选，其次取列表最前（服务端已按 score 排序）
                var ordered = new List<object[]>();
                for (int i = 0; i < cands.Length && i < 8; i++) ordered.Add(new[] { cands[i] });
                ordered.Sort((a, b) => SingerRank(b[0], artist).CompareTo(SingerRank(a[0], artist)));

                int tried = 0;
                for (int i = 0; i < ordered.Count && tried < MaxLyricsCandidates; i++)
                {
                    object c = ordered[i][0];
                    string id = JStr(JGet(c, "id"));
                    string accessKey = JStr(JGet(c, "accesskey"));
                    if (id.Length == 0 || accessKey.Length == 0) continue;
                    tried++;
                    try
                    {
                        SleepRate();
                        string dlUrl = "https://krcs.kugou.com/download?ver=1&man=yes&client=mobi" +
                                       "&id=" + Uri.EscapeDataString(id) +
                                       "&accesskey=" + Uri.EscapeDataString(accessKey) +
                                       "&fmt=lrc&charset=utf8";
                        Dictionary<string, object> droot = JObj(HttpGet(dlUrl, null));
                        string content = droot != null ? JStr(JGet(droot, "content")) : "";
                        if (content.Length > 0)
                        {
                            byte[] bytes = Convert.FromBase64String(content);
                            string lrc = Encoding.UTF8.GetString(bytes);
                            SaveLyrics(r, lrc);
                            if (r.LyricsMatched) break;
                        }
                    }
                    catch (FormatException) { }
                    catch (Exception) { }
                }
            }

            // 封面：Image 模板（{size}→400）优先，AlbumID 兜底
            string coverUrl = BuildCoverUrl(bestImage, bestAlbumId);
            if (coverUrl != null)
            {
                SleepRate();
                SaveCover(r, coverUrl);
            }

            return r.LyricsMatched || r.CoverMatched;
        }

        /// <summary>标题一致 +100；歌手一致 +40（部分匹配 +20）；原版标记 +20。标题无关返回 0。</summary>
        static int Score(string songName, string singerName, string queryTitle, string queryArtist, int isOriginal)
        {
            string nn = Normalize(songName);
            string nt = Normalize(queryTitle);
            if (nn.Length == 0) return 0;

            int score;
            if (nn == nt) score = 100;
            else if (nn.IndexOf(nt, StringComparison.Ordinal) >= 0 ||
                     nt.IndexOf(nn, StringComparison.Ordinal) >= 0)
                score = 50;
            else return 0;

            string sn = Normalize(singerName);
            string qa = Normalize(queryArtist);
            if (sn.Length > 0 && qa.Length > 0)
            {
                if (sn == qa) score += 40;
                else if (sn.IndexOf(qa, StringComparison.Ordinal) >= 0 ||
                         qa.IndexOf(sn, StringComparison.Ordinal) >= 0)
                    score += 20;
            }

            if (isOriginal == 1) score += 20;
            return score;
        }

        /// <summary>剥离搜索结果中的 &lt;em&gt; 高亮标签。</summary>
        static string StripEm(string s)
        {
            if (s == null) return "";
            return s.Replace("<em>", "").Replace("</em>", "");
        }

        static int SingerRank(object candidate, string artist)
        {
            string singer = StripEm(JStr(JGet(candidate, "singer")));
            if (artist.Length == 0) return 0;
            string qa = Normalize(artist);
            string sn = Normalize(singer);
            if (sn.Length == 0) return 0;
            if (sn == qa) return 2;
            if (sn.IndexOf(qa, StringComparison.Ordinal) >= 0 ||
                qa.IndexOf(sn, StringComparison.Ordinal) >= 0) return 1;
            return 0;
        }

        static string BuildCoverUrl(string imageTemplate, string albumId)
        {
            if (imageTemplate != null && imageTemplate.Length > 0)
            {
                string u = imageTemplate.Replace("{size}", "400");
                if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    u = "https://" + u.Substring(7);
                return u;
            }
            if (albumId != null && albumId.Length > 0)
                return "https://imge.kugou.com/stdmusic/400/" + albumId + ".jpg";
            return null;
        }
    }

    /* ============================================================
     * 数据源二：网易云公开接口（无需注册）
     * 注意：老搜索接口前排常是翻唱，必须打分过滤；新版 pc 接口需绑手机不可用
     * ============================================================ */

    public sealed class NeteaseProvider : LyricsProviderBase
    {
        public override string Name { get { return "网易云"; } }

        public override bool Match(NetMatchResult r, string musicPath, string title, string artist)
        {
            string query = string.IsNullOrEmpty(artist) ? title : title + " " + artist;
            string searchUrl = "https://music.163.com/api/search/get/web?s=" +
                               Uri.EscapeDataString(query) + "&type=1&limit=20&offset=0";

            Dictionary<string, object> sroot = JObj(HttpGet(searchUrl, "https://music.163.com/"));
            object result = sroot != null ? JGet(sroot, "result") : null;
            object[] songs = result != null ? JArr(JGet(result, "songs")) : null;
            if (songs == null || songs.Length == 0) return false;

            long bestId = 0;
            int bestScore = -1;
            for (int i = 0; i < songs.Length; i++)
            {
                object song = songs[i];
                long id = JLong(JGet(song, "id"));
                string name = JStr(JGet(song, "name"));
                if (id == 0) continue;

                bool artistHit = false, artistExact = false;
                object[] arts = JArr(JGet(song, "artists"));
                if (arts != null)
                {
                    for (int j = 0; j < arts.Length; j++)
                    {
                        string an = JStr(JGet(arts[j], "name"));
                        if (an.Length == 0) continue;
                        if (string.Equals(an, artist, StringComparison.OrdinalIgnoreCase)) artistExact = true;
                        if (an.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            artist.IndexOf(an, StringComparison.OrdinalIgnoreCase) >= 0)
                            artistHit = true;
                    }
                }

                int score = Score(name, title, artistHit, artistExact);
                if (score > bestScore) { bestScore = score; bestId = id; }
            }
            if (bestId == 0 || bestScore < MinAcceptableScore) return false;

            // 封面：歌曲详情
            SleepRate();
            string detailUrl = "https://music.163.com/api/song/detail/?id=" + bestId +
                               "&ids=" + Uri.EscapeDataString("[" + bestId + "]");
            try
            {
                Dictionary<string, object> droot = JObj(HttpGet(detailUrl, "https://music.163.com/"));
                object[] dsongs = droot != null ? JArr(JGet(droot, "songs")) : null;
                if (dsongs != null && dsongs.Length > 0)
                {
                    object album = JGet(dsongs[0], "album");
                    string pic = album != null ? JStr(JGet(album, "picUrl")) : "";
                    if (pic.Length > 0)
                    {
                        SleepRate();
                        SaveCover(r, pic);
                    }
                }
            }
            catch { }

            // 歌词
            SleepRate();
            string lyricUrl = "https://music.163.com/api/song/lyric?id=" + bestId + "&lv=1&kv=1&tv=-1";
            try
            {
                Dictionary<string, object> lroot = JObj(HttpGet(lyricUrl, "https://music.163.com/"));
                object lrc = lroot != null ? JGet(lroot, "lrc") : null;
                string lyric = lrc != null ? JStr(JGet(lrc, "lyric")) : "";
                SaveLyrics(r, lyric);
            }
            catch { }

            return r.LyricsMatched || r.CoverMatched;
        }

        /// <summary>标题一致 +100；歌手精确 +40（部分 +20）。标题无关返回 0。</summary>
        static int Score(string songName, string queryTitle, bool artistHit, bool artistExact)
        {
            string nn = Normalize(songName);
            string nt = Normalize(queryTitle);
            if (nn.Length == 0) return 0;

            int score;
            if (nn == nt) score = 100;
            else if (nn.IndexOf(nt, StringComparison.Ordinal) >= 0 ||
                     nt.IndexOf(nn, StringComparison.Ordinal) >= 0)
                score = 50;
            else return 0;

            if (artistExact) score += 40;
            else if (artistHit) score += 20;
            return score;
        }
    }
}
