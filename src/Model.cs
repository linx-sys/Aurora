/* ============================================================
 * Model.cs — 轨道模型 / 音乐库扫描 / 设置持久化
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Aurora
{
    public class Track : INotifyPropertyChanged
    {
        public string FilePath;
        public string FileName;
        public byte[] Cover;
        public string LrcText;
        public TimeSpan Duration;
        public long Bytes;

        string _title, _artist, _album;
        bool _isPlaying;

        public string Title { get { return _title; } set { _title = value; Raise("Title"); } }
        public string Artist { get { return _artist; } set { _artist = value; Raise("Artist"); } }
        public string Album { get { return _album; } set { _album = value; Raise("Album"); } }
        public bool IsPlaying { get { return _isPlaying; } set { _isPlaying = value; Raise("IsPlaying"); } }

        public string DurationText
        {
            get
            {
                if (Duration <= TimeSpan.Zero) return "--:--";
                return ((int)Duration.TotalMinutes) + ":" + Duration.Seconds.ToString("00");
            }
        }

        public string SubText
        {
            get
            {
                string a = string.IsNullOrEmpty(Artist) ? "未知歌手" : Artist;
                // 专辑名与歌名相同（单曲常见）时不再重复显示
                if (!string.IsNullOrEmpty(Album) && !string.Equals(Album, Title, StringComparison.CurrentCultureIgnoreCase))
                    return a + " · " + Album;
                return a;
            }
        }

        public bool HasLrc { get { return !string.IsNullOrEmpty(LrcText); } }

        public string IndexText { get; set; }

        public void RefreshDurationText() { Raise("DurationText"); }

        ImageSource _thumb;
        /// <summary>列表缩略图：真封面或按歌名生成（首次访问时生成并缓存）。</summary>
        public System.Windows.Media.ImageSource Thumb
        {
            get
            {
                if (_thumb == null)
                {
                    try { _thumb = CoverArt.ForTrack(this, 96); }
                    catch { }
                }
                return _thumb;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string prop)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(prop));
            if (prop == "Artist" || prop == "Album") Raise("SubText");
        }
    }

    public static class Library
    {
        static readonly string[] AudioExt = { ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".oga", ".aac", ".opus", ".wma" };

        public static bool IsAudio(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(AudioExt, e) >= 0;
        }

        /// <summary>递归枚举目录下的音频与歌词文件。</summary>
        public static void EnumerateFiles(string dir, List<string> outFiles, int depth)
        {
            if (depth > 6) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    if (e == ".lrc" || IsAudio(f)) outFiles.Add(f);
                }
            }
            catch { }
            if (depth < 6)
            {
                try
                {
                    foreach (string d in Directory.GetDirectories(dir))
                        EnumerateFiles(d, outFiles, depth + 1);
                }
                catch { }
            }
        }

        /// <summary>从一批文件构建轨道（音频 + 同名 lrc 配对），后台线程调用。</summary>
        public static List<Track> BuildTracks(IEnumerable<string> files)
        {
            var lrcMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var audio = new List<string>();
            foreach (string f in files)
            {
                if (f.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase))
                    lrcMap[Path.GetFileNameWithoutExtension(f)] = f;
                else if (IsAudio(f)) audio.Add(f);
            }

            var result = new List<Track>();
            foreach (string path in audio)
            {
                try { result.Add(FromPath(path, lrcMap)); } catch { }
            }
            result.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        public static Track FromPath(string path, Dictionary<string, string> lrcMap)
        {
            var t = new Track
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
            };
            try { t.Bytes = new FileInfo(path).Length; } catch { }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            string tagTitle = null, tagArtist = null, tagAlbum = null;
            if (ext == ".mp3")
            {
                Id3Info m = Id3.Read(path);
                tagTitle = m.Title;
                tagArtist = m.Artist;
                tagAlbum = m.Album;
                if (m.Cover != null) t.Cover = m.Cover;
                TimeSpan? d = Mp3Duration.Read(path);
                if (d.HasValue) t.Duration = d.Value;
            }
            else
            {
                // FLAC / M4A / OGG / OPUS / WAV 原生标签 + 封面 + 时长
                AudioTags at = Tags.Read(path);
                if (at != null)
                {
                    tagTitle = at.Title;
                    tagArtist = at.Artist;
                    tagAlbum = at.Album;
                    if (at.Cover != null) t.Cover = at.Cover;
                    if (at.Duration.HasValue) t.Duration = at.Duration.Value;
                }
            }

            // 文件名解析："NN - Artist - Title" / "Artist - Title" / "Artist-Title" / "Title"
            string baseName = Path.GetFileNameWithoutExtension(path).Trim();
            string stripped = Regex.Replace(baseName, @"^\s*\d{1,3}\s*[-._)]\s*", "");
            string fnameTitle = stripped, fnameArtist = null;
            if (stripped.Contains(" - "))
            {
                string[] parts = stripped.Split(new[] { " - " }, StringSplitOptions.None);
                if (parts.Length >= 2)
                {
                    fnameArtist = parts[0].Trim();
                    fnameTitle = stripped.Substring(parts[0].Length + 3).Trim();
                }
            }
            else
            {
                // 无空格写法回退：张韶涵-无度 / 吴昊–此去半生
                foreach (string sep in new[] { "-", "–", "—" })
                {
                    int idx = stripped.IndexOf(sep);
                    if (idx <= 0 || idx + sep.Length >= stripped.Length) continue;
                    string a = stripped.Substring(0, idx).Trim();
                    string b = stripped.Substring(idx + sep.Length).Trim();
                    if (a.Length > 0 && b.Length > 0 && a.Length <= 24)
                    {
                        fnameArtist = a;
                        fnameTitle = b;
                        break;
                    }
                }
            }

            // 垃圾标签检测：下载器写入的 title/artist/album 同值（如 kuwo）或平台占位词。
            // 此时文件名通常携带更真实的信息，弃用整个标签组。
            bool junkTag = !string.IsNullOrWhiteSpace(tagTitle) &&
                           (IsJunkTagText(tagTitle) ||
                            (tagTitle == tagArtist && tagTitle == tagAlbum) ||
                            (tagTitle == tagArtist && fnameArtist != null));
            if (junkTag)
            {
                tagTitle = null;
                tagArtist = null;
                tagAlbum = null;
            }

            t.Title = !string.IsNullOrWhiteSpace(tagTitle) ? tagTitle : fnameTitle;
            t.Artist = !string.IsNullOrWhiteSpace(tagArtist) ? tagArtist : (fnameArtist ?? "");
            t.Album = tagAlbum ?? "";
            if (string.IsNullOrEmpty(t.Title)) t.Title = Path.GetFileName(path);

            string lrc;
            if (lrcMap != null && lrcMap.TryGetValue(Path.GetFileNameWithoutExtension(path), out lrc))
            {
                try { t.LrcText = Id3.DecodeLoose(File.ReadAllBytes(lrc)); } catch { }
            }
            else
            {
                string alt = path.Substring(0, path.Length - ext.Length) + ".mp3.lrc";
                if (File.Exists(alt))
                {
                    try { t.LrcText = Id3.DecodeLoose(File.ReadAllBytes(alt)); } catch { }
                }
            }

            if (t.Artist == null) t.Artist = "";
            if (t.Album == null) t.Album = "";
            return t;
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

    /// <summary>MP3 时长估算：读帧头 bitrate/samplerate；有 Xing/Info 头时按帧数精确计算。</summary>
    public static class Mp3Duration
    {
        static readonly int[] BitratesV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        static readonly int[] BitratesV2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        static readonly int[] SampleRates = { 44100, 48000, 32000, 0 }; // MPEG1；MPEG2/2.5 再减半

        public static TimeSpan? Read(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long pos = 0;
                    var head = new byte[10];
                    if (fs.Read(head, 0, 10) < 10) return null;
                    if (head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33)
                    {
                        int size = ((head[6] & 0x7f) << 21) | ((head[7] & 0x7f) << 14) | ((head[8] & 0x7f) << 7) | (head[9] & 0x7f);
                        pos = 10L + size + ((head[5] & 0x10) != 0 ? 10 : 0);
                    }

                    fs.Position = pos;
                    var buf = new byte[65536];
                    int n = fs.Read(buf, 0, buf.Length);

                    for (int i = 0; i < n - 4; i++)
                    {
                        if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                        int ver = (buf[i + 1] >> 3) & 3;      // 3=MPEG1 2=MPEG2 0=MPEG2.5
                        int layer = (buf[i + 1] >> 1) & 3;    // 1=Layer III
                        int brIdx = (buf[i + 2] >> 4) & 0xF;
                        int srIdx = (buf[i + 2] >> 2) & 3;
                        if (ver == 1 || layer != 1 || brIdx == 0 || brIdx == 15 || srIdx == 3) continue;

                        int sampleRate = SampleRates[srIdx];
                        if (ver == 2) sampleRate /= 2;
                        else if (ver == 0) sampleRate /= 4;
                        int[] brTable = ver == 3 ? BitratesV1L3 : BitratesV2L3;
                        int bitrate = brTable[brIdx] * 1000;
                        if (bitrate == 0 || sampleRate == 0) continue;
                        int spf = ver == 3 ? 1152 : 576;

                        // 帧内找 Xing/Info 头
                        int frameLen = spf / 8 * bitrate / sampleRate + ((buf[i + 2] >> 1) & 1);
                        int limit = Math.Min(i + frameLen, n - 16);
                        for (int j = i + 4; j < limit; j++)
                        {
                            bool xing = buf[j] == 'X' && buf[j + 1] == 'i' && buf[j + 2] == 'n' && buf[j + 3] == 'g';
                            bool info = !xing && buf[j] == 'I' && buf[j + 1] == 'n' && buf[j + 2] == 'f' && buf[j + 3] == 'o';
                            if (xing || info)
                            {
                                int flags = (buf[j + 4] << 24) | (buf[j + 5] << 16) | (buf[j + 6] << 8) | buf[j + 7];
                                if ((flags & 1) != 0)
                                {
                                    int frames = (buf[j + 8] << 24) | (buf[j + 9] << 16) | (buf[j + 10] << 8) | buf[j + 11];
                                    if (frames > 0)
                                        return TimeSpan.FromSeconds(frames * (double)spf / sampleRate);
                                }
                                break;
                            }
                        }

                        // CBR 估算
                        long audioBytes = Math.Max(0, fs.Length - pos);
                        return TimeSpan.FromSeconds(audioBytes * 8.0 / bitrate);
                    }
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>简易 INI 设置（%APPDATA%\AuroraPlayer\settings.ini）。</summary>
    public static class Settings
    {
        static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraPlayer");
        static readonly string File_ = Path.Combine(Dir, "settings.ini");
        static Dictionary<string, string> _kv;

        static Dictionary<string, string> Load()
        {
            if (_kv != null) return _kv;
            _kv = new Dictionary<string, string>();
            try
            {
                foreach (string line in File.ReadAllLines(File_))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    _kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return _kv;
        }

        public static string Get(string key, string def)
        {
            string v;
            return Load().TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string val)
        {
            Load()[key] = val;
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                foreach (var kv in _kv) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(File_, sb.ToString());
            }
            catch { }
        }
    }
}
