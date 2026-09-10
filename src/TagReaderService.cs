/* ============================================================
 * TagReaderService.cs — 统一标签读取服务
 * 从 Model.Track 提取，封装 MP3/FLAC/M4A/OGG/OPUS/WAV/WMA
 * 的标签、封面、时长解析，以及文件名回退推断。
 * ============================================================ */
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Aurora
{
    /// <summary>统一的轨道元数据模型。</summary>
    public class TrackMetadata
    {
        public string? Title;
        public string? Artist;
        public string? Album;
        public byte[]? Cover;
        public TimeSpan? Duration;
    }

    /// <summary>
    /// 统一标签读取服务。支持 9 种音频格式，自动选择对应解析器，
    /// 标签缺失时用文件名推断回退。
    /// </summary>
    public static class TagReaderService
    {
        /// <summary>读取文件的完整元数据（标题/歌手/专辑/封面/时长）。</summary>
        public static TrackMetadata Read(string path)
        {
            var meta = new TrackMetadata();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return meta;

            string ext = Path.GetExtension(path).ToLowerInvariant();

            try
            {
                if (ext == ".mp3")
                {
                    Id3Info m = Id3.Read(path);
                    if (m != null)
                    {
                        meta.Title = m.Title;
                        meta.Artist = m.Artist;
                        meta.Album = m.Album;
                        if (m.Cover != null) meta.Cover = m.Cover;
                    }
                    TimeSpan? d = Mp3Duration.Read(path);
                    if (d.HasValue) meta.Duration = d.Value;
                }
                else
                {
                    // FLAC / M4A / OGG / OPUS / WAV / WMA / AAC 原生标签
                    AudioTags at = Tags.Read(path);
                    if (at != null)
                    {
                        meta.Title = at.Title;
                        meta.Artist = at.Artist;
                        meta.Album = at.Album;
                        if (at.Cover != null) meta.Cover = at.Cover;
                        if (at.Duration.HasValue) meta.Duration = at.Duration.Value;
                    }
                }
            }
            catch { /* 损坏文件：标签读取失败，回退到文件名推断 */ }

            // AAC 裸流（.aac，ADTS）无容器标签：解析 ADTS 帧头估算时长，
            // 列表构建阶段即可显示，不再等到首次播放
            if (ext == ".aac" && !meta.Duration.HasValue)
            {
                TimeSpan? adts = AacDuration.Read(path);
                if (adts.HasValue) meta.Duration = adts.Value;
            }

            // 先解析文件名推断（不直接写入 meta）："NN - Artist - Title" / "Artist - Title" / "Artist-Title"
            string? fnameArtist, fnameTitle;
            ParseFilename(path, out fnameArtist, out fnameTitle);

            // 智能识别下载器占位标签（title=artist=album=kuwo 等）→ 置空，
            // 让下面的文件名推断接管。注意顺序：必须先清除垃圾标签再回退，
            // 否则回退时 Title 仍是 "kuwo"（非空）不触发，清除后只能拿整文件名
            // 当标题（如「陈奕迅 - 富士山下」显示成歌名+未知歌手）。
            SanitizePlaceholderTags(meta, fnameArtist);

            // 标签缺失/被清除 → 用文件名拆分结果补全歌手与标题
            if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = fnameTitle;
            if (string.IsNullOrWhiteSpace(meta.Artist)) meta.Artist = fnameArtist;

            // 最终兜底：文件名也拆不出歌手时，标题用完整文件名
            if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = Path.GetFileNameWithoutExtension(path);
            if (meta.Title == null) meta.Title = "";
            if (meta.Artist == null) meta.Artist = "";
            if (meta.Album == null) meta.Album = "";

            return meta;
        }

        /// <summary>
        /// 解析文件名推断歌手与标题（不写入 meta，由调用方决定是否采用）：
        /// "NN - Artist - Title" / "Artist - Title" / "Artist-Title"。
        /// </summary>
        static void ParseFilename(string path, out string? artist, out string title)
        {
            string baseName = Path.GetFileNameWithoutExtension(path).Trim();
            string stripped = Regex.Replace(baseName, @"^\s*\d{1,3}\s*[-._)]\s*", "");
            title = stripped;
            artist = null;

            if (stripped.Contains(" - "))
            {
                string[] parts = stripped.Split(new[] { " - " }, StringSplitOptions.None);
                if (parts.Length >= 2)
                {
                    artist = parts[0].Trim();
                    title = stripped.Substring(parts[0].Length + 3).Trim();
                }
            }
            else
            {
                foreach (string sep in new[] { "-", "–", "—" })
                {
                    int idx = stripped.IndexOf(sep);
                    if (idx <= 0 || idx + sep.Length >= stripped.Length) continue;
                    string a = stripped.Substring(0, idx).Trim();
                    string b = stripped.Substring(idx + sep.Length).Trim();
                    if (a.Length > 0 && b.Length > 0 && a.Length <= 24)
                    {
                        artist = a;
                        title = b;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(title)) title = baseName;
        }

        /// <summary>识别并清除下载器写入的占位标签（如 kuwo、qq 等）。</summary>
        static void SanitizePlaceholderTags(TrackMetadata meta, string? fnameArtist)
        {
            bool junkTag = !string.IsNullOrWhiteSpace(meta.Title) &&
                           (IsJunkTagText(meta.Title) ||
                            (meta.Title == meta.Artist && meta.Title == meta.Album) ||
                            (meta.Title == meta.Artist && fnameArtist != null));
            if (junkTag)
            {
                meta.Title = null;
                meta.Artist = null;
                meta.Album = null;
            }
        }

        /// <summary>下载工具常写入的无意义占位标签词。</summary>
        static bool IsJunkTagText(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "kuwo":
                case "kwmusic":
                case "kuwo.cn":
                case "qqmusic":
                case "netease":
                case "cloudmusic":
                case "music":
                case "audio":
                case "temp":
                case "track":
                case "unknown":
                case "未知":
                case "未知歌手":
                case "未知艺术家":
                    return true;
                default:
                    return false;
            }
        }
    }
}
