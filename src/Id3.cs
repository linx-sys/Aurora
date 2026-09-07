/* ============================================================
 * Id3.cs — MP3 ID3 标签解析（v1 / v2.2 / v2.3 / v2.4）
 * 中文兼容：latin1 字节先试 UTF-8 严格解码，失败回退 GBK。
 * ============================================================ */
using System;
using System.IO;
using System.Text;

namespace Aurora
{
    public class Id3Info
    {
        public string Title;
        public string Artist;
        public string Album;
        public string Year;
        public int TrackNo = -1;
        public byte[] Cover;
        public string CoverMime;
        public string Version;
    }

    public static class Id3
    {
        static readonly Encoding Gbk = MakeGbk();
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        static Encoding MakeGbk()
        {
            try { return Encoding.GetEncoding(936); }
            catch { return null; }
        }

        public static Id3Info Read(string path)
        {
            var info = new Id3Info();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ReadStream(fs, info);
                }
            }
            catch { /* 尽力而为 */ }
            return info;
        }

        /// <summary>从内存中的完整 ID3v2 标签数据解析（WAV id3 chunk 使用）。</summary>
        public static Id3Info ReadFromBytes(byte[] data, int len)
        {
            var info = new Id3Info();
            try
            {
                using (var ms = new MemoryStream(data, 0, len))
                {
                    ReadStream(ms, info);
                }
            }
            catch { }
            return info;
        }

        static void ReadStream(Stream fs, Id3Info info)
        {
            ReadV2(fs, info);
            ReadV1(fs, info);
        }

        /* ---------------- ID3v2 ---------------- */

        static void ReadV2(Stream fs, Id3Info info)
        {
            if (fs.Length < 10) return;
            byte[] head = ReadBytes(fs, 0, 10);
            if (head[0] != 0x49 || head[1] != 0x44 || head[2] != 0x33) return;

            int ver = head[3];
            int flags = head[5];
            int size = Synchsafe(head, 6);
            if (size <= 0 || size > fs.Length) return;

            info.Version = "ID3v2." + ver;
            // 整体 unsynchronised（v2.3 及以下）罕见，放弃解析避免乱码
            if ((flags & 0x80) != 0 && ver < 4) return;

            long pos = 10;
            long tagEnd = 10L + size;

            if ((flags & 0x40) != 0) // extended header
            {
                byte[] eh = ReadBytes(fs, pos, 4);
                if (eh.Length < 4) return;
                int extSize = ver >= 4 ? Synchsafe(eh, 0) : Be32(eh, 0);
                pos += ver >= 4 ? extSize : 4 + extSize;
            }

            int idLen = ver == 2 ? 3 : 4;
            int fhLen = ver == 2 ? 6 : 10;

            while (pos + fhLen <= tagEnd)
            {
                byte[] fh = ReadBytes(fs, pos, fhLen);
                if (fh.Length < fhLen) break;
                string id = Ascii(fh, 0, idLen);
                if (!IsFrameId(id)) break;

                int fsize;
                if (ver == 2) fsize = (fh[3] << 16) | (fh[4] << 8) | fh[5];
                else if (ver == 3) fsize = Be32(fh, 4);
                else fsize = Synchsafe(fh, 4);
                if (fsize <= 0 || pos + fhLen + fsize > tagEnd) break;

                int fFlags = ver >= 3 ? Be16(fh, 8) : 0;
                long dataStart = pos + fhLen;
                int dataLen = fsize;
                if (ver == 4 && (fFlags & 0x0001) != 0) dataLen -= 4; // 数据长度指示

                byte[] data = ReadBytes(fs, dataStart, Math.Max(0, dataLen));
                ApplyFrame(id, data, ver, info);
                pos = dataStart + fsize;
            }
        }

        static bool IsFrameId(string s)
        {
            if (s.Length < 3) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        static void ApplyFrame(string id, byte[] data, int ver, Id3Info info)
        {
            if (data == null || data.Length == 0) return;

            string textKey = null;
            if (ver == 2)
            {
                if (id == "TT2") textKey = "title";
                else if (id == "TP1") textKey = "artist";
                else if (id == "TAL") textKey = "album";
                else if (id == "TYE") textKey = "year";
                else if (id == "TRK") textKey = "track";
            }
            else
            {
                if (id == "TIT2") textKey = "title";
                else if (id == "TPE1") textKey = "artist";
                else if (id == "TALB") textKey = "album";
                else if (id == "TYER" || id == "TDRC") textKey = "year";
                else if (id == "TRCK") textKey = "track";
            }

            if (textKey != null)
            {
                // 首字节是编码标记，正文从第二字节开始
                string text = CleanStr(DecodeText(Slice(data, 1, data.Length - 1), data[0]));
                if (text.Length == 0) return;
                switch (textKey)
                {
                    case "title": if (info.Title == null) info.Title = text; break;
                    case "artist": if (info.Artist == null) info.Artist = text; break;
                    case "album": if (info.Album == null) info.Album = text; break;
                    case "year": if (info.Year == null) info.Year = text; break;
                    case "track":
                        if (info.TrackNo < 0)
                        {
                            int v;
                            if (FirstInt(text, out v)) info.TrackNo = v;
                        }
                        break;
                }
                return;
            }

            if (id == "APIC" || id == "PIC")
            {
                if (info.Cover == null) ParsePicture(data, id == "PIC", info);
            }
        }

        static void ParsePicture(byte[] d, bool v22, Id3Info info)
        {
            try
            {
                int enc = d[0];
                int p;
                string mime;
                if (v22)
                {
                    mime = Ascii(d, 1, 3);
                    p = 4;
                }
                else
                {
                    int z = IndexOf(d, 0, 1);
                    if (z < 0) return;
                    mime = Ascii(d, 1, z - 1);
                    p = z + 1;
                }
                p += 1; // picture type
                if (enc == 1 || enc == 2)
                {
                    while (p + 1 < d.Length && !(d[p] == 0 && d[p + 1] == 0)) p += 2;
                    p += 2;
                }
                else
                {
                    int z2 = IndexOf(d, 0, p);
                    if (z2 < 0) return;
                    p = z2 + 1;
                }
                if (p >= d.Length - 64) return;

                string m = NormMime(mime);
                if (m == null) m = SniffImage(d, p);
                if (m == null) return;

                var cover = new byte[d.Length - p];
                Array.Copy(d, p, cover, 0, cover.Length);
                info.Cover = cover;
                info.CoverMime = m;
            }
            catch { }
        }

        static string NormMime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return null;
            string m = mime.Trim().ToLowerInvariant();
            if (m == "png" || m == "image/png") return "image/png";
            if (m == "jpg" || m == "jpeg" || m == "image/jpg" || m == "image/jpeg") return "image/jpeg";
            if (m == "gif" || m == "image/gif") return "image/gif";
            if (m == "bmp" || m == "image/bmp") return "image/bmp";
            if (m == "webp" || m == "image/webp") return "image/webp";
            if (m.StartsWith("image/")) return m;
            return null;
        }

        static string SniffImage(byte[] d, int p)
        {
            int n = d.Length - p;
            if (n > 8 && d[p] == 0x89 && d[p + 1] == 0x50 && d[p + 2] == 0x4e && d[p + 3] == 0x47) return "image/png";
            if (n > 3 && d[p] == 0xff && d[p + 1] == 0xd8) return "image/jpeg";
            if (n > 6 && d[p] == 0x47 && d[p + 1] == 0x49 && d[p + 2] == 0x46) return "image/gif";
            if (n > 2 && d[p] == 0x42 && d[p + 1] == 0x4d) return "image/bmp";
            if (n > 12 && d[p + 8] == 0x57 && d[p + 9] == 0x45 && d[p + 10] == 0x42 && d[p + 11] == 0x50) return "image/webp";
            return null;
        }

        /* ---------------- ID3v1 ---------------- */

        static void ReadV1(Stream fs, Id3Info info)
        {
            if (fs.Length < 128) return;
            byte[] tail = ReadBytes(fs, fs.Length - 128, 128);
            if (tail.Length < 128) return;
            if (Ascii(tail, 0, 3) != "TAG") return;
            if (info.Version == null) info.Version = "ID3v1";

            if (info.Title == null && tail[3] != 0) info.Title = CleanStr(DecodeLoose(Slice(tail, 3, 30)));
            if (info.Artist == null && tail[33] != 0) info.Artist = CleanStr(DecodeLoose(Slice(tail, 33, 30)));
            if (info.Album == null && tail[63] != 0) info.Album = CleanStr(DecodeLoose(Slice(tail, 63, 30)));
            if (info.Year == null)
            {
                string y = CleanStr(Ascii(tail, 93, 4));
                if (y.Length == 4 && IsDigits(y)) info.Year = y;
            }
            if (info.TrackNo < 0 && tail[125] == 0 && tail[126] != 0) info.TrackNo = tail[126];
        }

        /* ---------------- 文本解码 ---------------- */

        /// <summary>ID3 文本帧解码。enc: 0=latin1(常为GBK) 1=utf16bom 2=utf16be 3=utf8</summary>
        static string DecodeText(byte[] data, int enc)
        {
            if (data.Length == 0) return "";
            try
            {
                switch (enc)
                {
                    case 1:
                        {
                            int off = 0;
                            if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe) off = 2;
                            else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff)
                                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
                            return Encoding.Unicode.GetString(data, off, data.Length - off);
                        }
                    case 2: return Encoding.BigEndianUnicode.GetString(data);
                    case 3:
                        {
                            int off = (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) ? 3 : 0;
                            return new UTF8Encoding(false).GetString(data, off, data.Length - off);
                        }
                    default: return DecodeLoose(data);
                }
            }
            catch { return DecodeLoose(data); }
        }

        /// <summary>宽松解码：UTF-8 严格 → GBK → Latin1。</summary>
        public static string DecodeLoose(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            // 去 BOM
            if (b.Length >= 3 && b[0] == 0xef && b[1] == 0xbb && b[2] == 0xbf)
                b = Slice(b, 3, b.Length - 3);
            else if (b.Length >= 2 && b[0] == 0xff && b[1] == 0xfe) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            else if (b.Length >= 2 && b[0] == 0xfe && b[1] == 0xff) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);

            try { return new UTF8Encoding(false, true).GetString(b); }
            catch { }
            if (Gbk != null)
            {
                try { return Gbk.GetString(b); } catch { }
            }
            return Latin1.GetString(b);
        }

        static string CleanStr(string s)
        {
            if (s == null) return null;
            s = s.Trim('\0', ' ', '\t', '\r', '\n');
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == 0) break;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /* ---------------- 字节工具 ---------------- */

        static byte[] ReadBytes(Stream fs, long pos, int len)
        {
            if (fs.Position != pos) fs.Position = pos;
            var buf = new byte[len];
            long n = 0;
            while (n < len)
            {
                int r = fs.Read(buf, (int)n, len - (int)n);
                if (r <= 0) break;
                n += r;
            }
            if (n == len) return buf;
            var shorter = new byte[n];
            Array.Copy(buf, shorter, n);
            return shorter;
        }

        static byte[] Slice(byte[] src, int start, int len)
        {
            if (start >= src.Length) return new byte[0];
            if (start + len > src.Length) len = src.Length - start;
            var r = new byte[len];
            Array.Copy(src, start, r, 0, len);
            return r;
        }

        static int Synchsafe(byte[] b, int off)
        {
            return (b[off] << 21) | (b[off + 1] << 14) | (b[off + 2] << 7) | b[off + 3];
        }

        static int Be32(byte[] b, int off)
        {
            return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
        }

        static int Be16(byte[] b, int off)
        {
            return (b[off] << 8) | b[off + 1];
        }

        static int IndexOf(byte[] b, byte v, int start)
        {
            for (int i = start; i < b.Length; i++) if (b[i] == v) return i;
            return -1;
        }

        static string Ascii(byte[] b, int start, int len)
        {
            var sb = new StringBuilder(len);
            int end = Math.Min(start + len, b.Length);
            for (int i = start; i < end; i++) sb.Append((char)b[i]);
            return sb.ToString();
        }

        static bool IsDigits(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        static bool FirstInt(string s, out int v)
        {
            v = 0;
            int i = 0;
            while (i < s.Length && (s[i] < '0' || s[i] > '9')) i++;
            int j = i;
            while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
            if (j == i) return false;
            return int.TryParse(s.Substring(i, j - i), out v);
        }
    }
}
