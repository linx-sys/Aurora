/* ============================================================
 * Model.cs — 轨道模型 / 音乐库扫描 / 设置持久化
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Aurora
{
    public class Track : INotifyPropertyChanged
    {
        public string FilePath = null!;
        public string FileName = null!;
        public byte[]? Cover;
        public string? LrcText;
        public TimeSpan Duration;
        public long Bytes;

        string? _title, _artist, _album;
        bool _isPlaying;

        public string? Title { get { return _title; } set { _title = value; Raise("Title"); } }
        public string? Artist { get { return _artist; } set { _artist = value; Raise("Artist"); } }
        public string? Album { get { return _album; } set { _album = value; Raise("Album"); } }
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

        public string? IndexText { get; set; }

        public void RefreshDurationText() { Raise("DurationText"); }

        ImageSource? _thumb;
        /// <summary>列表缩略图：真封面或按歌名生成（首次访问时生成并缓存）。</summary>
        public System.Windows.Media.ImageSource? Thumb
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

        public event PropertyChangedEventHandler? PropertyChanged;
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

        /// <summary>返回是否完整枚举；权限/IO 错误、深度限制或跳过链接时禁止清理旧库。</summary>
        public static bool EnumerateFiles(string dir, List<string> outFiles, int depth, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 6) return false;
            bool complete = true;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (f.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase) || IsAudio(f)) outFiles.Add(f);
                }
                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (depth == 6 || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        complete = false;
                    else if (!EnumerateFiles(child, outFiles, depth + 1, cancellationToken)) complete = false;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { complete = false; }
            catch (UnauthorizedAccessException) { complete = false; }
            return complete;
        }

        /// <summary>文件指纹：大小 + 最后写入时间 UTC（增量扫描复用判定依据）。</summary>
        public static void GetFingerprint(string path, out long bytes, out long lastModifiedUtcTicks)
        {
            bytes = 0; lastModifiedUtcTicks = 0;
            try
            {
                var fi = new FileInfo(path);
                bytes = fi.Length;
                lastModifiedUtcTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch { }
        }

        /// <summary>从一批文件构建轨道（音频 + 同名 lrc 配对），后台线程调用。全量解析（无缓存）。</summary>
        public static List<Track> BuildTracks(IEnumerable<string> files)
        {
            return BuildTracksIncremental(files, null, null);
        }

        /// <summary>
        /// 增量扫描构建轨道（P1）：db 非空时按指纹（大小+最后写入时间）命中缓存直接复用
        /// 标签元数据，未命中才解析并回写 DB；cleanupDir 非空时清理该目录下已消失文件的过期行。
        /// lrc 文本与外部封面兜底保持每次现读（联网匹配后会出现，不能缓存）。
        /// </summary>
        public static List<Track> BuildTracksIncremental(IEnumerable<string> files, ILibraryStore? db, string? cleanupDir,
            CancellationToken cancellationToken = default, bool enumerationComplete = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lrcMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var audio = new List<string>();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (f.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase))
                    lrcMap[LibraryPath.Normalize(Path.ChangeExtension(f, null))] = f;
                else if (IsAudio(f) && present.Add(LibraryPath.Normalize(f))) audio.Add(f);
            }

            var result = new List<Track>();
            var toUpsert = new List<TrackRow>();
            foreach (string path in audio)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { result.Add(BuildOne(path, lrcMap, db, toUpsert, cancellationToken)); }
                catch (OperationCanceledException) { throw; }
                catch { enumerationComplete = false; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase));

            if (db != null)
            {
                if (toUpsert.Count > 0) db.UpsertMany(toUpsert);
                cancellationToken.ThrowIfCancellationRequested();
                // present 来自枚举而不是解析结果：标签读取失败不等于文件已删除。
                if (enumerationComplete && !string.IsNullOrEmpty(cleanupDir)) db.DeleteMissingUnder(cleanupDir, present);
            }
            return result;
        }

        /// <summary>
        /// DB 优先秒开（P1-3）：由库行直接物化 Track，启动时无需等扫描。
        /// <paramref name="probeExtras"/> = true（默认）：逐文件探测（存在性 + 同名 lrc + 外部封面兜底），
        /// 数据完整但每首 3~5 次文件系统调用，超大库慢——供测试/小库使用。
        /// = false：零探测快路径（P2 优化，启动 ① 用）——不查存在性（已删文件成为短命幽灵行，
        /// 由随后的差分同步清理）、不读 lrc/外部封面（由 ② 权威同步补齐），仅 DB 内数据。
        /// </summary>
        public static List<Track> BuildTracksFromRows(IEnumerable<TrackRow> rows, bool probeExtras = true, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<Track>();
            foreach (TrackRow row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row == null || string.IsNullOrEmpty(row.Path)) continue;

                if (!probeExtras)
                {
                    try
                    {
                        result.Add(FromMetadata(row.Path, row.Title, row.Artist, row.Album,
                            TimeSpan.FromSeconds(row.DurationSeconds), row.Cover, null, readFsExtras: false, cancellationToken: cancellationToken));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    continue;
                }

                if (!File.Exists(row.Path)) continue;   // 已消失：等后台差分同步清理 DB 行
                var lrcMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // 同名 lrc 探测：xxx.lrc 优先，其次 xxx.mp3.lrc（与扫描期配对规则一致）
                string baseName = Path.Combine(Path.GetDirectoryName(row.Path) ?? "", Path.GetFileNameWithoutExtension(row.Path));
                string lrc = baseName + ".lrc";
                if (!File.Exists(lrc)) lrc = baseName + Path.GetExtension(row.Path) + ".lrc";
                if (File.Exists(lrc)) lrcMap[LibraryPath.Normalize(baseName)] = lrc;

                try
                {
                    result.Add(FromMetadata(row.Path, row.Title, row.Artist, row.Album,
                        TimeSpan.FromSeconds(row.DurationSeconds), row.Cover, lrcMap, cancellationToken: cancellationToken));
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        /// <summary>构建单条轨道：缓存命中走 FromMetadata 复用，未命中解析并记入回写队列。</summary>
        static Track BuildOne(string path, Dictionary<string, string> lrcMap, ILibraryStore? db, List<TrackRow> toUpsert, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Library.GetFingerprint(path, out long bytes, out long mtime);
            TrackRow? row = db != null ? db.TryGet(path) : null;
            if (LibraryDatabase.FingerprintMatches(row, bytes, mtime))
            {
                // 缓存命中：跳过昂贵的标签解析（ID3/FLAC/M4A/封面）
                return FromMetadata(path, row!.Title, row.Artist, row.Album,
                    TimeSpan.FromSeconds(row.DurationSeconds), row.Cover, lrcMap, cancellationToken: cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            TrackMetadata meta = TagReaderService.Read(path);
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan duration = meta.Duration ?? TimeSpan.Zero;
            if (db != null)
            {
                toUpsert.Add(new TrackRow
                {
                    Path = path,
                    FileName = Path.GetFileName(path),
                    Title = meta.Title,
                    Artist = meta.Artist,
                    Album = meta.Album,
                    DurationSeconds = duration.TotalSeconds,
                    Bytes = bytes,
                    LastModified = mtime,
                    Cover = meta.Cover,
                });
            }
            return FromMetadata(path, meta.Title, meta.Artist, meta.Album, duration, meta.Cover, lrcMap, cancellationToken: cancellationToken);
        }

        /// <summary>全量解析单文件并构建轨道（含 lrc 配对与外部封面兜底）。</summary>
        public static Track FromPath(string path, Dictionary<string, string>? lrcMap)
        {
            TrackMetadata meta = TagReaderService.Read(path);
            return FromMetadata(path, meta.Title, meta.Artist, meta.Album,
                meta.Duration ?? TimeSpan.Zero, meta.Cover, lrcMap);
        }

        /// <summary>由元数据（解析所得或缓存复用）构建轨道；lrc 与外部封面兜底每次现读。
        /// readFsExtras=false 时不做任何文件系统访问（零探测快路径，P2 优化）。</summary>
        public static Track FromMetadata(string path, string? title, string? artist, string? album,
            TimeSpan duration, byte[]? tagCover, Dictionary<string, string>? lrcMap, bool readFsExtras = true,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = new Track
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
            };
            if (readFsExtras) { try { t.Bytes = new FileInfo(path).Length; } catch { } }

            t.Title = title;
            t.Artist = artist;
            t.Album = album;
            if (tagCover != null && tagCover.Length > 0) t.Cover = tagCover;
            if (duration > TimeSpan.Zero) t.Duration = duration;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            cancellationToken.ThrowIfCancellationRequested();
            if (readFsExtras) ApplyLrc(t, lrcMap);
            cancellationToken.ThrowIfCancellationRequested();

            if (t.Artist == null) t.Artist = "";
            if (t.Album == null) t.Album = "";

            // 外部封面兜底：缓存目录优先，同目录 .jpg 向后兼容
            if (readFsExtras) ApplyExternalCoverFallback(t);
            cancellationToken.ThrowIfCancellationRequested();
            return t;
        }

        /// <summary>歌词文件定位与读取：联网匹配缓存优先 → 同名 lrc → xxx.mp3.lrc。</summary>
        static void ApplyLrc(Track t, Dictionary<string, string>? lrcMap)
        {
            string path = t.FilePath;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string? lrc = NetMatch.FindLyricFile(path);
            if (lrc == null && lrcMap != null)
            {
                lrcMap.TryGetValue(LibraryPath.Normalize(Path.ChangeExtension(path, null)), out lrc);
                if (lrc == null) lrcMap.TryGetValue(LibraryPath.Normalize(path), out lrc);
            }
            if (lrc == null)
            {
                string sibling = Path.ChangeExtension(path, ".lrc");
                if (File.Exists(sibling)) lrc = sibling;
                else if (File.Exists(path + ".lrc")) lrc = path + ".lrc";
            }
            if (lrc != null)
            {
                try { t.LrcText = Id3.DecodeLoose(File.ReadAllBytes(lrc)); } catch { }
            }
        }

        /// <summary>无内嵌封面时：联网匹配封面缓存 → 同目录同名 .jpg。</summary>
        static void ApplyExternalCoverFallback(Track t)
        {
            if (t.Cover != null && t.Cover.Length > 0) return;
            string cached = NetMatch.GetCoverCachePath(t.FilePath);
            if (File.Exists(cached))
                try { t.Cover = File.ReadAllBytes(cached); } catch { }
            if (t.Cover == null || t.Cover.Length == 0)
            {
                string jpg = Path.ChangeExtension(t.FilePath, ".jpg");
                try { if (File.Exists(jpg)) t.Cover = File.ReadAllBytes(jpg); } catch { }
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

    /// <summary>可指定文件的设置存储。测试使用临时路径，不接触默认用户设置。</summary>
    public sealed class SettingsStore
    {
        const string Header = ";Aurora.Settings.v2.base64";
        readonly string filePath;
        readonly object gate = new object();
        Dictionary<string, string>? values;

        public SettingsStore(string path) { filePath = Path.GetFullPath(path); }

        Dictionary<string, string> Load()
        {
            if (values != null) return values;
            var loaded = new Dictionary<string, string>();
            try
            {
                string[] lines = File.ReadAllLines(filePath);
                bool encoded = lines.Length > 0 && lines[0] == Header;
                foreach (string line in lines)
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1);
                    if (encoded)
                    {
                        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
                        catch (FormatException) { continue; }
                    }
                    else value = value.Trim();
                    loaded[key] = value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return values = loaded;
        }

        public string Get(string key, string def)
        {
            lock (gate) return Load().TryGetValue(key, out string? value) ? value : def;
        }

        public int GetInt(string key, int def, int min = int.MinValue, int max = int.MaxValue)
        {
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && value >= min && value <= max ? value : def;
        }

        public double GetDouble(string key, double def, double min = double.MinValue, double max = double.MaxValue)
        {
            string text = Get(key, "");
            // 旧版部分调用用本地文化写入；兼容逗号小数，但不接受千位分组。
            bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
            return parsed && !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max ? value : def;
        }

        public T GetEnum<T>(string key, T def) where T : struct, Enum
        {
            return Enum.TryParse<T>(Get(key, ""), true, out T value) && Enum.IsDefined(typeof(T), value) ? value : def;
        }

        public bool GetBool(string key, bool def)
        {
            string text = Get(key, "");
            if (text == "1") return true;
            if (text == "0") return false;
            return bool.TryParse(text, out bool value) ? value : def;
        }

        public void Set(string key, string val)
        {
            if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(new[] { '=', '\r', '\n' }) >= 0)
                throw new ArgumentException("设置键不能包含等号或换行", nameof(key));
            lock (gate)
            {
                Load()[key] = val ?? "";
                string? temporary = null;
                try
                {
                    string directory = Path.GetDirectoryName(filePath)!;
                    Directory.CreateDirectory(directory);
                    temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                    var sb = new StringBuilder(Header).Append("\r\n");
                    foreach (var entry in values!)
                        sb.Append(entry.Key).Append('=').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Value))).Append("\r\n");
                    byte[] data = new UTF8Encoding(false).GetBytes(sb.ToString());
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(data, 0, data.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
                    else File.Move(temporary, filePath);
                    temporary = null;
                }
                catch (Exception ex)
                {
                    try { MainViewModel.Dbg("Settings.Set FAIL [" + key + "]: " + ex.Message); } catch { }
                }
                finally
                {
                    if (temporary != null) { try { File.Delete(temporary); } catch { } }
                }
            }
        }
    }

    public static class Settings
    {
        static readonly SettingsStore Store = new SettingsStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraPlayer", "settings.ini"));
        public static string Get(string key, string def) => Store.Get(key, def);
        public static int GetInt(string key, int def, int min = int.MinValue, int max = int.MaxValue) => Store.GetInt(key, def, min, max);
        public static double GetDouble(string key, double def, double min = double.MinValue, double max = double.MaxValue) => Store.GetDouble(key, def, min, max);
        public static T GetEnum<T>(string key, T def) where T : struct, Enum => Store.GetEnum(key, def);
        public static bool GetBool(string key, bool def) => Store.GetBool(key, def);
        public static void Set(string key, string val) => Store.Set(key, val);
    }
}
