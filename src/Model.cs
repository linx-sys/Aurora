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

        /// <summary>封面变化后调用（如联网匹配下载了封面）：丢弃缓存并通知绑定刷新。</summary>
        public void InvalidateThumb()
        {
            _thumb = null;
            Raise("Thumb");
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

            // 统一标签读取服务（封装 MP3/FLAC/M4A/OGG/OPUS/WAV 解析 + 文件名推断 + 垃圾标签清除）
            TrackMetadata meta = TagReaderService.Read(path);
            t.Title = meta.Title;
            t.Artist = meta.Artist;
            t.Album = meta.Album;
            if (meta.Cover != null) t.Cover = meta.Cover;
            if (meta.Duration.HasValue) t.Duration = meta.Duration.Value;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            string lrc = NetMatch.FindLyricFile(path);
            if (lrc == null && lrcMap != null)
                lrcMap.TryGetValue(Path.GetFileNameWithoutExtension(path), out lrc);
            if (lrc == null)
            {
                string alt = path.Substring(0, path.Length - ext.Length) + ".mp3.lrc";
                if (File.Exists(alt)) lrc = alt;
            }
            if (lrc != null)
            {
                try { t.LrcText = Id3.DecodeLoose(File.ReadAllBytes(lrc)); } catch { }
            }

            if (t.Artist == null) t.Artist = "";
            if (t.Album == null) t.Album = "";

            // 外部封面兜底：缓存目录优先，同目录 .jpg 向后兼容
            if (t.Cover == null || t.Cover.Length == 0)
            {
                string cached = NetMatch.GetCoverCachePath(path);
                if (File.Exists(cached))
                    try { t.Cover = File.ReadAllBytes(cached); } catch { }
            }
            if (t.Cover == null || t.Cover.Length == 0)
            {
                string jpg = Path.ChangeExtension(path, ".jpg");
                try { if (File.Exists(jpg)) t.Cover = File.ReadAllBytes(jpg); } catch { }
            }
            return t;
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

    /// <summary>AAC 裸流（ADTS）时长估算：解析首个 ADTS 帧头得到采样率与帧长，
    /// 按 CBR 思路（同 Mp3Duration）以首帧均长估算总时长；每帧固定 1024 样本。</summary>
    public static class AacDuration
    {
        static readonly int[] SampleRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

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
                        // 允许 ADTS 前置 ID3v2 标签
                        int size = ((head[6] & 0x7f) << 21) | ((head[7] & 0x7f) << 14) | ((head[8] & 0x7f) << 7) | (head[9] & 0x7f);
                        pos = 10L + size + ((head[5] & 0x10) != 0 ? 10 : 0);
                    }

                    fs.Position = pos;
                    var buf = new byte[65536];
                    int n = fs.Read(buf, 0, buf.Length);

                    // ADTS 同步字：byte0=0xFF；byte1 高 4 位=1111 且 layer 位（bit2:1）=00
                    for (int i = 0; i < n - 7; i++)
                    {
                        if (buf[i] != 0xFF || (buf[i + 1] & 0xF6) != 0xF0) continue;
                        int srIdx = (buf[i + 2] >> 2) & 0xF;
                        if (srIdx >= SampleRates.Length) continue;
                        int sampleRate = SampleRates[srIdx];
                        if (sampleRate == 0) continue;
                        int headerLen = (buf[i + 1] & 1) != 0 ? 7 : 9;   // protection_absent → 有无 CRC
                        int frameLen = ((buf[i + 3] & 0x03) << 11) | (buf[i + 4] << 3) | ((buf[i + 5] >> 5) & 0x07);
                        if (frameLen <= headerLen) continue;

                        long audioBytes = Math.Max(0, fs.Length - pos);
                        double frames = audioBytes / (double)frameLen;   // CBR 帧均长估算
                        return TimeSpan.FromSeconds(frames * 1024 / sampleRate);
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
            catch (Exception ex)
            {
                // 写盘失败不能让播放器崩掉（内存中的 _kv 已更新），
                // 但也不能完全静默——至少留下痕迹便于诊断"设置不生效"类问题
                try
                {
                    MainViewModel.Dbg("Settings.Set FAIL [" + key + "=" + val + "]: " + ex.Message);
                }
                catch { }
            }
        }
    }
}
