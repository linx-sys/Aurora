/* ============================================================
 * Tags.cs — 非 MP3 格式的标签/封面/时长解析
 *   FLAC  : STREAMINFO 时长 + VORBIS_COMMENT + PICTURE 封面
 *   M4A   : MP4 atom 遍历（mvhd 时长 + ilst 标签 + covr 封面）
 *   OGG   : OggS 页重组（识别码 + OpusTags/VorbisComment + 尾页 granule 时长）
 *   WAV   : fmt/data 时长 + LIST INFO 标签 + id3 chunk（复用 Id3 解析）
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aurora
{
    public class AudioTags
    {
        public string Title, Artist, Album, Year;
        public TimeSpan? Duration;
        public byte[] Cover;
        public string CoverMime;
    }

    public static class Tags
    {
        public static AudioTags Read(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".flac": return FlacTags.Read(path);
                    case ".m4a":
                    case ".m4b":
                    case ".mp4": return Mp4Tags.Read(path);
                    case ".ogg":
                    case ".oga":
                    case ".opus": return OggTags.Read(path);
                    case ".wav": return WavTags.Read(path);
                }
            }
            catch { }
            return null;
        }
    }

    /* ============================ FLAC ============================ */

    static class FlacTags
    {
        public static AudioTags Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var head = new byte[4];
                if (fs.Read(head, 0, 4) < 4 || head[0] != 'f' || head[1] != 'L' || head[2] != 'a' || head[3] != 'C')
                    return null;

                var t = new AudioTags();
                var hdr = new byte[4];
                while (fs.Read(hdr, 0, 4) == 4)
                {
                    bool last = (hdr[0] & 0x80) != 0;
                    int type = hdr[0] & 0x7F;
                    int len = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                    if (len < 0 || len > 64 * 1024 * 1024) break;

                    if (type == 0 && len >= 18) // STREAMINFO
                    {
                        var b = new byte[len];
                        if (fs.Read(b, 0, len) != len) break;
                        int sampleRate = (b[10] << 12) | (b[11] << 4) | (b[12] >> 4);
                        long total = ((long)(b[13] & 0x0F) << 32) | ((long)b[14] << 24) | ((long)b[15] << 16) | ((long)b[16] << 8) | b[17];
                        if (sampleRate > 0 && total > 0)
                            t.Duration = TimeSpan.FromSeconds(total / (double)sampleRate);
                    }
                    else if (type == 4) // VORBIS_COMMENT
                    {
                        var b = new byte[len];
                        if (fs.Read(b, 0, len) != len) break;
                        TagsUtil.ApplyVorbisComment(b, t);
                    }
                    else if (type == 6) // PICTURE
                    {
                        var b = new byte[len];
                        if (fs.Read(b, 0, len) != len) break;
                        ReadFlacPicture(b, t);
                    }
                    else
                    {
                        fs.Position += len;
                    }

                    if (last) break;
                }
                return t;
            }
        }

        static void ReadFlacPicture(byte[] b, AudioTags t)
        {
            int p = 4; // picture type
            int mimeLen = TagsUtil.Be32(b, p); p += 4;
            string mime = TagsUtil.Ascii(b, p, mimeLen); p += mimeLen;
            int descLen = TagsUtil.Be32(b, p); p += 4 + descLen;
            p += 16; // width/height/depth/colors
            int dataLen = TagsUtil.Be32(b, p); p += 4;
            if (dataLen > 0 && dataLen <= b.Length - p)
            {
                t.Cover = TagsUtil.Slice(b, p, dataLen);
                t.CoverMime = string.IsNullOrEmpty(mime) ? "image/jpeg" : mime;
            }
        }
    }

    /* ============================ M4A / MP4 ============================ */

    static class Mp4Tags
    {
        public static AudioTags Read(string path)
        {
            byte[] buf;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    buf = new byte[fs.Length];
                    if (fs.Read(buf, 0, buf.Length) != buf.Length) return null;
                }
            }
            catch { return null; }

            var t = new AudioTags();
            Walk(buf, 0, buf.Length, 0, t);
            return t;
        }

        static void Walk(byte[] b, int start, int end, int depth, AudioTags t)
        {
            if (depth > 8) return;
            int p = start;
            while (p + 8 <= end)
            {
                long size = TagsUtil.Be32(b, p);
                string type = TagsUtil.Ascii(b, p + 4, 4);
                int headLen = 8;
                if (size == 1) // 64-bit size
                {
                    if (p + 16 > end) break;
                    size = ((long)TagsUtil.Be32(b, p + 8) << 32) | (uint)TagsUtil.Be32(b, p + 12);
                    headLen = 16;
                }
                else if (size == 0)
                {
                    size = end - p;
                }
                if (size < headLen || p + size > end) break;
                int bodyStart = p + headLen;
                int bodyEnd = p + (int)size;

                switch (type)
                {
                    case "moov":
                    case "udta":
                    case "ilst":
                    case "trak":
                    case "mdia":
                        Walk(b, bodyStart, bodyEnd, depth + 1, t);
                        break;
                    case "meta":
                        Walk(b, bodyStart + 4, bodyEnd, depth + 1, t); // version+flags 前缀
                        break;
                    case "mvhd":
                        ReadMvhd(b, bodyStart, bodyEnd, t);
                        break;
                    case "©nam": ReadDataText(b, bodyStart, bodyEnd, v => t.Title = v); break;
                    case "©ART": ReadDataText(b, bodyStart, bodyEnd, v => t.Artist = v); break;
                    case "©alb": ReadDataText(b, bodyStart, bodyEnd, v => t.Album = v); break;
                    case "©day": ReadDataText(b, bodyStart, bodyEnd, delegate(string v)
                    {
                        if (!string.IsNullOrEmpty(v)) t.Year = v.Substring(0, Math.Min(4, v.Length));
                    }); break;
                    case "covr": ReadDataCover(b, bodyStart, bodyEnd, t); break;
                }
                p = bodyEnd;
            }
        }

        static void ReadMvhd(byte[] b, int start, int end, AudioTags t)
        {
            try
            {
                if (start + 24 > end) return;
                int version = b[start];
                if (version == 1)
                {
                    // v1: ctime(8) mtime(8) timescale(4) duration(8)
                    if (start + 32 > end) return;
                    int timescale = TagsUtil.Be32(b, start + 20);
                    long dur = ((long)TagsUtil.Be32(b, start + 24) << 32) | (uint)TagsUtil.Be32(b, start + 28);
                    if (timescale > 0 && dur > 0) t.Duration = TimeSpan.FromSeconds(dur / (double)timescale);
                }
                else
                {
                    int timescale = TagsUtil.Be32(b, start + 12);
                    int dur = TagsUtil.Be32(b, start + 16);
                    if (timescale > 0 && dur > 0) t.Duration = TimeSpan.FromSeconds(dur / (double)timescale);
                }
            }
            catch { }
        }

        static void ReadDataText(byte[] b, int start, int end, Action<string> assign)
        {
            int p = start;
            while (p + 8 <= end)
            {
                int size = TagsUtil.Be32(b, p);
                string type = TagsUtil.Ascii(b, p + 4, 4);
                if (size < 8 || p + size > end) break;
                if (type == "data" && size >= 16)
                {
                    int dataType = TagsUtil.Be32(b, p + 8) & 0xFFFFFF;
                    if (dataType == 1) // UTF-8 text
                        assign(Encoding.UTF8.GetString(b, p + 16, size - 16).Trim());
                    return;
                }
                p += size;
            }
        }

        static void ReadDataCover(byte[] b, int start, int end, AudioTags t)
        {
            int p = start;
            while (p + 8 <= end)
            {
                int size = TagsUtil.Be32(b, p);
                string type = TagsUtil.Ascii(b, p + 4, 4);
                if (size < 8 || p + size > end) break;
                if (type == "data" && size >= 16)
                {
                    int dataType = TagsUtil.Be32(b, p + 8) & 0xFFFFFF;
                    byte[] img = new byte[size - 16];
                    Array.Copy(b, p + 16, img, 0, img.Length);
                    if (img.Length > 64)
                    {
                        t.Cover = img;
                        t.CoverMime = dataType == 14 ? "image/png" : "image/jpeg";
                    }
                    return;
                }
                p += size;
            }
        }
    }

    /* ============================ OGG (Vorbis / Opus) ============================ */

    static class OggTags
    {
        public static AudioTags Read(string path)
        {
            var t = new AudioTags();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 64) return null;

                // --- 前部：识别包头 + 注释包 ---
                byte[] head = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
                int headLen = fs.Read(head, 0, head.Length);

                int sampleRate = 48000;
                bool isOpus = false;
                double preSkip = 0;
                List<byte[]> packets = CollectPackets(head, headLen, 2);
                if (packets.Count == 0) return null;

                byte[] id = packets[0];
                if (id.Length > 7 && id[0] == 1 && id[1] == 'v' && id[2] == 'o' && id[3] == 'r' && id[4] == 'b' && id[5] == 'i' && id[6] == 's')
                {
                    // \x01vorbis + version(4) + channels(1) + rate(4LE)
                    if (id.Length >= 16)
                        sampleRate = TagsUtil.Le32(id, 12);
                }
                else if (id.Length >= 8 && id[0] == 'O' && id[1] == 'p' && id[2] == 'u' && id[3] == 's' && id[4] == 'H' && id[5] == 'e' && id[6] == 'a' && id[7] == 'd')
                {
                    isOpus = true;
                    if (id.Length >= 12) preSkip = TagsUtil.Le16(id, 10);
                }
                else return null;

                if (packets.Count >= 2)
                {
                    byte[] cmt = packets[1];
                    int off = 0;
                    if (!isOpus && cmt.Length > 7 && cmt[0] == 3) off = 7;         // \x03vorbis
                    else if (isOpus && cmt.Length > 8) off = 8;                     // OpusTags
                    if (off > 0)
                    {
                        var body = new byte[cmt.Length - off];
                        Array.Copy(cmt, off, body, 0, body.Length);
                        TagsUtil.ApplyVorbisComment(body, t);
                    }
                }

                // --- 尾部：最后一个 OggS 页的 granule position ---
                long tailPos = Math.Max(0, fs.Length - 65536);
                fs.Position = tailPos;
                byte[] tail = new byte[fs.Length - tailPos];
                int tailLen = fs.Read(tail, 0, tail.Length);
                long granule = -1;
                for (int i = tailLen - 27; i >= 0; i--)
                {
                    if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S')
                    {
                        granule = ((long)TagsUtil.Le32(tail, i + 10) << 32) | (uint)TagsUtil.Le32(tail, i + 6);
                        break;
                    }
                }
                if (granule > 0)
                {
                    double sec = granule / (isOpus ? 48000.0 : sampleRate);
                    if (isOpus) sec = Math.Max(0, sec - preSkip / 48000.0);
                    if (sec > 0) t.Duration = TimeSpan.FromSeconds(sec);
                }
                return t;
            }
        }

        /// <summary>从缓冲区顺序解析 Ogg 页，重组前 wantCount 个逻辑包。</summary>
        static List<byte[]> CollectPackets(byte[] buf, int len, int wantCount)
        {
            var result = new List<byte[]>();
            var cur = new MemoryStream();
            bool packetOpen = false;
            int p = 0;

            while (p + 27 <= len && result.Count < wantCount)
            {
                if (!(buf[p] == 'O' && buf[p + 1] == 'g' && buf[p + 2] == 'g' && buf[p + 3] == 'S')) { p++; continue; }
                int segCount = buf[p + 26];
                if (p + 27 + segCount > len) break;

                int q = p + 27 + segCount; // payload 起点
                if (q >= len) break;

                for (int i = 0; i < segCount; i++)
                {
                    int seg = buf[p + 27 + i];
                    int take = Math.Min(seg, len - q);
                    if (take > 0) cur.Write(buf, q, take);
                    q += seg;
                    if (seg < 255) // 段长度 < 255 表示逻辑包结束
                    {
                        if (cur.Length > 0)
                        {
                            result.Add(cur.ToArray());
                            cur.SetLength(0);
                            if (result.Count >= wantCount) return result;
                        }
                        packetOpen = false;
                    }
                    else
                    {
                        packetOpen = true;
                    }
                }
                p = q;
            }
            // 跨页且被缓冲区截断的半开包也返回（注释头一般在前 256KB 内足够）
            if (packetOpen && cur.Length > 0 && result.Count < wantCount)
                result.Add(cur.ToArray());
            return result;
        }
    }

    /* ============================ WAV ============================ */

    static class WavTags
    {
        public static AudioTags Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var head = new byte[12];
                if (fs.Read(head, 0, 12) < 12) return null;
                if (TagsUtil.Ascii(head, 0, 4) != "RIFF" || TagsUtil.Ascii(head, 8, 4) != "WAVE") return null;

                var t = new AudioTags();
                long byteRate = 0;
                long riffEnd = fs.Length;

                while (fs.Position + 8 <= riffEnd)
                {
                    var hdr = new byte[8];
                    if (fs.Read(hdr, 0, 8) < 8) break;
                    string id = TagsUtil.Ascii(hdr, 0, 4);
                    int size = (int)((uint)hdr[4] | ((uint)hdr[5] << 8) | ((uint)hdr[6] << 16) | ((uint)hdr[7] << 24));
                    if (size < 0 || fs.Position + size > riffEnd + 1024) break;
                    long next = fs.Position + size + (size & 1); // WORD 对齐

                    if (id == "fmt " && size >= 16)
                    {
                        var fmt = new byte[Math.Min(size, 40)];
                        fs.Read(fmt, 0, fmt.Length);
                        byteRate = TagsUtil.Le32(fmt, 8);
                    }
                    else if (id == "data")
                    {
                        if (byteRate > 0 && size > 0)
                            t.Duration = TimeSpan.FromSeconds(size / (double)byteRate);
                    }
                    else if (id == "LIST" && size > 4)
                    {
                        var list = new byte[Math.Min(size, 65536)];
                        int read = fs.Read(list, 0, list.Length);
                        if (read > 4 && TagsUtil.Ascii(list, 0, 4) == "INFO") ReadWavInfo(list, 4, read, t);
                    }
                    else if ((id == "id3 " || id == "ID3 ") && size > 0)
                    {
                        var id3 = new byte[size];
                        int read = fs.Read(id3, 0, id3.Length);
                        if (read > 10)
                        {
                            Id3Info m = Id3.ReadFromBytes(id3, read);
                            if (m != null)
                            {
                                t.Title = m.Title;
                                t.Artist = m.Artist;
                                t.Album = m.Album;
                                t.Year = m.Year;
                                if (m.Cover != null) { t.Cover = m.Cover; t.CoverMime = m.CoverMime; }
                            }
                        }
                    }

                    fs.Position = Math.Min(next, riffEnd);
                    if (size <= 0) break;
                }
                return t;
            }
        }

        static void ReadWavInfo(byte[] b, int start, int len, AudioTags t)
        {
            int p = start;
            while (p + 8 <= len)
            {
                string id = TagsUtil.Ascii(b, p, 4);
                int size = TagsUtil.Le32(b, p + 4);
                if (size < 0 || p + 8 + size > len) break;
                string val = Id3.DecodeLoose(TagsUtil.Slice(b, p + 8, size)).Trim();
                switch (id)
                {
                    case "INAM": if (t.Title == null) t.Title = val; break;
                    case "IART": if (t.Artist == null) t.Artist = val; break;
                    case "IPRD": if (t.Album == null) t.Album = val; break;
                    case "ICRD": if (t.Year == null && val.Length >= 4) t.Year = val.Substring(0, 4); break;
                }
                p += 8 + size + (size & 1);
            }
        }
    }

    /* ============================ 共用工具 ============================ */

    static class TagsUtil
    {
        public static int Be32(byte[] b, int p)
        {
            return (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
        }

        public static int Le32(byte[] b, int p)
        {
            return (int)((uint)b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24));
        }

        public static int Le16(byte[] b, int p)
        {
            return b[p] | (b[p + 1] << 8);
        }

        public static string Ascii(byte[] b, int p, int len)
        {
            var sb = new StringBuilder(len);
            int end = Math.Min(p + len, b.Length);
            for (int i = p; i < end; i++) sb.Append((char)b[i]);
            return sb.ToString();
        }

        public static byte[] Slice(byte[] src, int start, int len)
        {
            if (start >= src.Length) return new byte[0];
            if (start + len > src.Length) len = src.Length - start;
            var r = new byte[len];
            Array.Copy(src, start, r, 0, len);
            return r;
        }

        /// <summary>解析 VorbisComment 结构（FLAC 注释块 / Ogg 注释包共用）。</summary>
        public static void ApplyVorbisComment(byte[] b, AudioTags t)
        {
            try
            {
                int p = 0;
                int vendorLen = Le32(b, p); p += 4 + vendorLen;
                if (p + 4 > b.Length) return;
                int count = Le32(b, p); p += 4;
                for (int i = 0; i < count && p + 4 <= b.Length; i++)
                {
                    int len = Le32(b, p); p += 4;
                    if (len < 0 || p + len > b.Length) break;
                    string entry = Encoding.UTF8.GetString(b, p, len);
                    p += len;
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = entry.Substring(0, eq).ToUpperInvariant();
                    string val = entry.Substring(eq + 1).Trim();
                    if (val.Length == 0) continue;
                    switch (key)
                    {
                        case "TITLE": if (t.Title == null) t.Title = val; break;
                        case "ARTIST": if (t.Artist == null) t.Artist = val; break;
                        case "ALBUM": if (t.Album == null) t.Album = val; break;
                        case "DATE": if (t.Year == null && val.Length >= 4) t.Year = val.Substring(0, 4); break;
                    }
                }
            }
            catch { }
        }
    }
}
