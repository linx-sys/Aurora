/* ============================================================
 * Id3Tests.cs — MP3 ID3 标签解析单元测试
 * 覆盖：ID3v1 / v2.2 / v2.3 / v2.4 文本帧、GBK 中文兼容、
 *       轨道号、APIC 封面、损坏/非法输入的容错、DecodeLoose 解码链。
 * ============================================================ */
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Aurora.Tests
{
    public class Id3Tests : TempFileTestBase, IDisposable
    {
        static Id3Tests()
        {
            // .NET Core/10 默认不含代码页编码；测试进程需显式注册（与 App 启动逻辑一致），
            // 否则 Id3 的 GBK 回退链退化为 Latin1
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        /* ---------------- 字节构造辅助 ---------------- */

        static byte[] Synchsafe(int size)
        {
            return new byte[] {
                (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F),
                (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F) };
        }

        static byte[] Be32Bytes(int v)
        {
            return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        /// <summary>构造 ID3v2.3 帧（4 字节 ID + Be32 大小 + 2 字节标志 + 数据）。</summary>
        static byte[] V23Frame(string id, byte[] data)
        {
            var b = new byte[10 + data.Length];
            Encoding.ASCII.GetBytes(id, 0, id.Length, b, 0);
            Be32Bytes(data.Length).CopyTo(b, 4);
            data.CopyTo(b, 10);
            return b;
        }

        /// <summary>构造 ID3v2.4 帧（同步安全帧大小）。</summary>
        static byte[] V24Frame(string id, byte[] data)
        {
            var b = new byte[10 + data.Length];
            Encoding.ASCII.GetBytes(id, 0, id.Length, b, 0);
            Synchsafe(data.Length).CopyTo(b, 4);
            b[8] = 0; b[9] = 0;
            data.CopyTo(b, 10);
            return b;
        }

        static byte[] V22Frame(string id, byte[] data)
        {
            var b = new byte[6 + data.Length];
            Encoding.ASCII.GetBytes(id, 0, id.Length, b, 0);
            b[3] = (byte)((data.Length >> 16) & 0xFF);
            b[4] = (byte)((data.Length >> 8) & 0xFF);
            b[5] = (byte)(data.Length & 0xFF);
            data.CopyTo(b, 6);
            return b;
        }

        static byte[] Id3Header(int ver, int flags, int bodySize)
        {
            var head = new byte[10];
            head[0] = (byte)'I'; head[1] = (byte)'D'; head[2] = (byte)'3';
            head[3] = (byte)ver;
            head[5] = (byte)flags;
            Synchsafe(bodySize).CopyTo(head, 6);
            return head;
        }

        static byte[] Concat(params byte[][] parts)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var p in parts) ms.Write(p, 0, p.Length);
                return ms.ToArray();
            }
        }

        /// <summary>文本帧数据：编码字节 + 文本。</summary>
        static byte[] TextFrameData(byte enc, string text, Encoding enc2)
        {
            var body = enc2.GetBytes(text);
            var data = new byte[1 + body.Length];
            data[0] = enc;
            body.CopyTo(data, 1);
            return data;
        }

        /* ---------------- ID3v2.3 ---------------- */

        [Fact]
        public void V23_Utf8TextFrames()
        {
            var tag = Concat(
                Id3Header(3, 0,
                    V23Frame("TIT2", TextFrameData(3, "晴天", new UTF8Encoding(false))).Length +
                    V23Frame("TPE1", TextFrameData(3, "周杰伦", new UTF8Encoding(false))).Length +
                    V23Frame("TALB", TextFrameData(3, "叶惠美", new UTF8Encoding(false))).Length),
                V23Frame("TIT2", TextFrameData(3, "晴天", new UTF8Encoding(false))),
                V23Frame("TPE1", TextFrameData(3, "周杰伦", new UTF8Encoding(false))),
                V23Frame("TALB", TextFrameData(3, "叶惠美", new UTF8Encoding(false))));

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal("ID3v2.3", info.Version);
            Assert.Equal("晴天", info.Title);
            Assert.Equal("周杰伦", info.Artist);
            Assert.Equal("叶惠美", info.Album);
        }

        [Fact]
        public void V23_Utf16WithBom()
        {
            var frame = V23Frame("TIT2", TextFrameData(1, "Test", Encoding.Unicode));
            var tag = Concat(Id3Header(3, 0, frame.Length), frame);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal("Test", info.Title);
        }

        [Fact]
        public void V23_Enc0_GbkBytes_DecodedViaGbkFallback()
        {
            // "音乐" 的 GBK 字节（D2 F4 C0 D6）不是合法 UTF-8 → 应走 GBK 回退
            var frame = V23Frame("TIT2", new byte[] { 0, 0xD2, 0xF4, 0xC0, 0xD6 });
            var tag = Concat(Id3Header(3, 0, frame.Length), frame);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal("音乐", info.Title);
        }

        [Fact]
        public void V23_TrackNumber_ExtractsLeadingInt()
        {
            var frame = V23Frame("TRCK", TextFrameData(3, "3/12", new UTF8Encoding(false)));
            var tag = Concat(Id3Header(3, 0, frame.Length), frame);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal(3, info.TrackNo);
        }

        [Fact]
        public void V23_ApicCover_PngMime()
        {
            // enc(1) + "image/png\0" + type(1) + desc"\0" + PNG 魔数 + 填充
            var img = new byte[8 + 70];
            img[0] = 0x89; img[1] = 0x50; img[2] = 0x4E; img[3] = 0x47;
            img[4] = 0x0D; img[5] = 0x0A; img[6] = 0x1A; img[7] = 0x0A;
            var mime = Encoding.ASCII.GetBytes("image/png\0");
            var data = new byte[1 + mime.Length + 1 + 1 + img.Length];
            int p = 0;
            data[p++] = 0;                       // latin1
            mime.CopyTo(data, p); p += mime.Length;
            data[p++] = 3;                       // picture type: front cover
            data[p++] = 0;                       // description 终止符
            img.CopyTo(data, p);

            var frame = V23Frame("APIC", data);
            var tag = Concat(Id3Header(3, 0, frame.Length), frame);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.NotNull(info.Cover);
            Assert.Equal("image/png", info.CoverMime);
            Assert.Equal(0x89, info.Cover[0]);
            Assert.Equal(img.Length, info.Cover.Length);
        }

        [Fact]
        public void V23_UnsynchronisedFlag_AbandonsParsing()
        {
            var tag = Concat(
                Id3Header(3, 0x80, 20),
                new byte[20]);   // 标志置位后按约定放弃帧解析，避免乱码

            var info = Id3.ReadFromBytes(tag, tag.Length);
            // 版本号在标志检查前已记录；帧内容被放弃 → 文本字段为空
            Assert.Equal("ID3v2.3", info.Version);
            Assert.Null(info.Title);
        }

        /* ---------------- ID3v2.4 ---------------- */

        [Fact]
        public void V24_SynchsafeFrameSizes_And_TDRCYear()
        {
            var t1 = V24Frame("TIT2", TextFrameData(3, "Title24", new UTF8Encoding(false)));
            var t2 = V24Frame("TDRC", TextFrameData(3, "2024", new UTF8Encoding(false)));
            var tag = Concat(Id3Header(4, 0, t1.Length + t2.Length), t1, t2);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal("ID3v2.4", info.Version);
            Assert.Equal("Title24", info.Title);
            Assert.Equal("2024", info.Year);
        }

        /* ---------------- ID3v2.2 ---------------- */

        [Fact]
        public void V22_ThreeCharFrames()
        {
            var t1 = V22Frame("TT2", TextFrameData(3, "Old", new UTF8Encoding(false)));
            var t2 = V22Frame("TP1", TextFrameData(3, "Artist22", new UTF8Encoding(false)));
            var tag = Concat(Id3Header(2, 0, t1.Length + t2.Length), t1, t2);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal("ID3v2.2", info.Version);
            Assert.Equal("Old", info.Title);
            Assert.Equal("Artist22", info.Artist);
        }

        /* ---------------- 非法输入容错 ---------------- */

        [Fact]
        public void NotId3Data_ReturnsEmptyInfo()
        {
            var junk = Encoding.ASCII.GetBytes("MPEG audio stream without tags........");
            var info = Id3.ReadFromBytes(junk, junk.Length);
            Assert.Null(info.Version);
            Assert.Null(info.Title);
        }

        [Fact]
        public void ZeroSizeHeader_ReturnsEmptyInfo()
        {
            var info = Id3.ReadFromBytes(Id3Header(3, 0, 0), 10);
            Assert.Null(info.Version);
        }

        [Fact]
        public void OversizedFrameSize_StopsGracefully()
        {
            // 帧大小字段写 0x7FFFFFFF（超出标签体）→ 应中断而非崩溃/越界
            var frame = new byte[10];
            Encoding.ASCII.GetBytes("TIT2", 0, 4, frame, 0);
            Be32Bytes(0x7FFFFFFF).CopyTo(frame, 4);
            var tag = Concat(Id3Header(3, 0, 10), frame, new byte[10]);

            var info = Id3.ReadFromBytes(tag, tag.Length);
            Assert.Equal("ID3v2.3", info.Version);   // 头有效
            Assert.Null(info.Title);                  // 帧被丢弃
        }

        [Fact]
        public void EmptyData_ReturnsEmptyInfo()
        {
            var info = Id3.ReadFromBytes(new byte[0], 0);
            Assert.Null(info.Title);
        }

        /* ---------------- ID3v1（文件尾） ---------------- */

        [Fact]
        public void V1_ReadFromTail()
        {
            var file = new byte[100 + 128];
            // TAG 尾：title/artist/album 各 30 + year 4 + comment 28 + 0 + track + genre
            int o = 100;
            file[o++] = (byte)'T'; file[o++] = (byte)'A'; file[o++] = (byte)'G';
            Encoding.ASCII.GetBytes("V1 Song").CopyTo(file, o); o += 30;
            Encoding.ASCII.GetBytes("V1 Artist").CopyTo(file, o); o += 30;
            Encoding.ASCII.GetBytes("V1 Album").CopyTo(file, o); o += 30;
            Encoding.ASCII.GetBytes("1999").CopyTo(file, o); o += 4;
            o += 28;                       // comment（v1.1 缩短）
            file[o++] = 0;                 // 125: v1.1 标记
            file[o++] = 7;                 // 126: 轨道号
            file[o] = 0xFF;                // 127: genre

            string path = WriteTemp("v1.mp3", file);
            var info = Id3.Read(path);
            Assert.Equal("ID3v1", info.Version);
            Assert.Equal("V1 Song", info.Title);
            Assert.Equal("V1 Artist", info.Artist);
            Assert.Equal("V1 Album", info.Album);
            Assert.Equal("1999", info.Year);
            Assert.Equal(7, info.TrackNo);
        }

        [Fact]
        public void V1_MissingFile_ReturnsEmptyInfo()
        {
            var info = Id3.Read(Path.Combine(Path.GetTempPath(), "aurora_tests_missing_" + Guid.NewGuid().ToString("N") + ".mp3"));
            Assert.Null(info.Title);
        }

        /* ---------------- DecodeLoose 解码链 ---------------- */

        [Fact]
        public void DecodeLoose_ValidUtf8()
        {
            Assert.Equal("abc123", Id3.DecodeLoose(Encoding.UTF8.GetBytes("abc123")));
        }

        [Fact]
        public void DecodeLoose_GbkFallback()
        {
            // GBK "音乐"：D2 F4 C0 D6（非法 UTF-8）
            Assert.Equal("音乐", Id3.DecodeLoose(new byte[] { 0xD2, 0xF4, 0xC0, 0xD6 }));
        }

        [Fact]
        public void DecodeLoose_Utf16LeBom()
        {
            Assert.Equal("hi", Id3.DecodeLoose(Encoding.Unicode.GetPreamble().Length > 0
                ? Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("hi"))
                : Encoding.Unicode.GetBytes("hi")));
        }

        [Fact]
        public void DecodeLoose_Utf8BomStripped()
        {
            Assert.Equal("ok", Id3.DecodeLoose(Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("ok"))));
        }

        [Fact]
        public void DecodeLoose_EmptyAndNull()
        {
            Assert.Equal("", Id3.DecodeLoose(null));
            Assert.Equal("", Id3.DecodeLoose(new byte[0]));
        }
    }
}
