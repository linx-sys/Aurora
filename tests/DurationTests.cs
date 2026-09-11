/* ============================================================
 * DurationTests.cs — Mp3Duration / AacDuration 单元测试
 * 通过合成最小合法帧流（内存构造 → 临时文件）验证时长估算，
 * 覆盖：ID3 跳过、Xing 帧数精确计算、CBR 估算、ADTS 帧头解析、
 *       非法输入返回 null。
 * ============================================================ */
using System;
using System.IO;
using Xunit;

namespace Aurora.Tests
{
    public class Mp3DurationTests : TempFileTestBase
    {
        static byte[] MakeId3(int size)
        {
            var b = new byte[10 + size];
            b[0] = (byte)'I'; b[1] = (byte)'D'; b[2] = (byte)'3';
            b[3] = 3; b[4] = 0;
            b[6] = (byte)((size >> 21) & 0x7F);
            b[7] = (byte)((size >> 14) & 0x7F);
            b[8] = (byte)((size >> 7) & 0x7F);
            b[9] = (byte)(size & 0x7F);
            return b;
        }

        /// <summary>MPEG1 LayerIII 128kbps 44100Hz 帧头 + Xing 头（frames 帧）+ 帧体填充。</summary>
        static byte[] MakeMp3WithXing(int frames, bool withId3)
        {
            using (var ms = new MemoryStream())
            {
                if (withId3) ms.Write(MakeId3(0), 0, 10);
                ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 0, 4);   // 帧头
                ms.Write(new byte[] { (byte)'X', (byte)'i', (byte)'n', (byte)'g' }, 0, 4);
                ms.Write(new byte[] { 0, 0, 0, 1 }, 0, 4);               // flags: frames 有效
                ms.Write(new byte[] { (byte)(frames >> 24), (byte)(frames >> 16), (byte)(frames >> 8), (byte)frames }, 0, 4);
                ms.Write(new byte[420], 0, 420);   // 帧体填充（Xing 扫描区间需要 ≥ frameLen 的数据）
                return ms.ToArray();
            }
        }

        [Fact]
        public void XingHeader_ExactDuration()
        {
            string path = WriteTemp("x.mp3", MakeMp3WithXing(100, withId3: false));
            var d = Mp3Duration.Read(path);
            Assert.NotNull(d);
            Assert.Equal(100 * 1152.0 / 44100, d.Value.TotalSeconds, 3);
        }

        [Fact]
        public void SkipsId3Header_BeforeFrameScan()
        {
            string path = WriteTemp("x.mp3", MakeMp3WithXing(50, withId3: true));
            var d = Mp3Duration.Read(path);
            Assert.NotNull(d);
            Assert.Equal(50 * 1152.0 / 44100, d.Value.TotalSeconds, 3);
        }

        [Fact]
        public void CbrEstimate_WhenNoXing()
        {
            // 无 Xing：128kbps 下 audioBytes * 8 / 128000 = 秒数
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 0, 4);
                ms.Write(new byte[128000], 0, 128000);
                string path = WriteTemp("x.mp3", ms.ToArray());
                var d = Mp3Duration.Read(path);
                Assert.NotNull(d);
                Assert.Equal(8.0, d.Value.TotalSeconds, 1);   // 128000B*8/128000bps = 8s
            }
        }

        [Fact]
        public void GarbageInput_ReturnsNull()
        {
            string path = WriteTemp("x.mp3", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            Assert.Null(Mp3Duration.Read(path));
        }
    }

    public class AacDurationTests : TempFileTestBase
    {
        /// <summary>构造一个 ADTS 帧：44100Hz、双声道（0b010）、总长 frameLen 字节（含 7 字节头）。</summary>
        static byte[] AdtsFrame(int frameLen)
        {
            var f = new byte[frameLen];
            f[0] = 0xFF;
            f[1] = 0xF1;             // sync + MPEG-4 + layer 00 + protection_absent=1（头 7 字节）
            f[2] = 0x90;             // profile LC(01) + srIdx=4 (44100) + private 0 + channel 最高位 0
            f[3] = (byte)(0x80 | ((frameLen >> 11) & 0x03));   // channel 低 2 位=10 + 帧长最高 2 位
            f[4] = (byte)((frameLen >> 3) & 0xFF);
            f[5] = (byte)(((frameLen & 0x07) << 5) | 0x1F);   // 帧长低 3 位 + buffer fullness 高 5 位
            f[6] = 0xFC;             // buffer fullness 低 6 位 + 2 个 AAC 帧
            return f;
        }

        static byte[] MakeAdts(int frameCount, int frameLen)
        {
            var frame = AdtsFrame(frameLen);
            var all = new byte[frameCount * frameLen];
            for (int i = 0; i < frameCount; i++)
                Array.Copy(frame, 0, all, i * frameLen, frameLen);
            return all;
        }

        [Fact]
        public void Adts_EstimatesDurationFromFrameLength()
        {
            // 20 帧 × 100 字节：时长 = 20 * 1024 / 44100 ≈ 0.4644s
            string path = WriteTemp("x.aac", MakeAdts(20, 100));
            var d = AacDuration.Read(path);
            Assert.NotNull(d);
            Assert.Equal(20 * 1024.0 / 44100, d.Value.TotalSeconds, 3);
        }

        [Fact]
        public void Adts_SkipsLeadingId3()
        {
            var id3 = new byte[10];
            id3[0] = (byte)'I'; id3[1] = (byte)'D'; id3[2] = (byte)'3';
            var frames = MakeAdts(10, 100);
            var all = new byte[10 + frames.Length];
            Array.Copy(id3, all, 10);
            Array.Copy(frames, 0, all, 10, frames.Length);
            string path = WriteTemp("x.aac", all);
            var d = AacDuration.Read(path);
            Assert.NotNull(d);
            Assert.Equal(10 * 1024.0 / 44100, d.Value.TotalSeconds, 3);
        }

        [Fact]
        public void GarbageInput_ReturnsNull()
        {
            string path = WriteTemp("x.aac", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Null(AacDuration.Read(path));
        }
    }

    /// <summary>临时文件基类：每个用例写独立临时文件，测试后清理。</summary>
    public abstract class TempFileTestBase : IDisposable
    {
        protected string WriteTemp(string name, byte[] bytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "aurora_tests_" + Guid.NewGuid().ToString("N") + "_" + name);
            File.WriteAllBytes(path, bytes);
            _temp = path;
            return path;
        }

        string? _temp;

        public void Dispose()
        {
            try { if (_temp != null && File.Exists(_temp)) File.Delete(_temp); } catch { }
        }
    }
}
