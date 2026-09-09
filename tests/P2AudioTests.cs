/* ============================================================
 * P2AudioTests.cs — 音频体验单元测试
 * 覆盖：EBU R128 响度分析（997Hz 校准、门限、K 权重频响）、
 *       ReplayGain 增益钳制、GainFadeSampleProvider 包络。
 * ============================================================ */
using System;
using NAudio.Wave;
using Xunit;

namespace Aurora.Tests
{
    public class LoudnessTests
    {
        /// <summary>生成立体声正弦（997Hz 标准校准频率）。</summary>
        static float[] Sine(double freq, double amplitude, double seconds, int sampleRate = 44100)
        {
            int frames = (int)(seconds * sampleRate);
            var data = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                double v = amplitude * Math.Sin(2 * Math.PI * freq * f / sampleRate);
                data[f * 2] = (float)v;
                data[f * 2 + 1] = (float)v;
            }
            return data;
        }

        [Fact]
        public void FullScale1kHzMonoSine_MatchesReferenceValue()
        {
            // 权威校准（pyloudnorm tests）：单声道 1kHz 满幅正弦 44.1kHz → -3.0523 LUFS
            int frames = 44100;
            var data = new float[frames];
            for (int f = 0; f < frames; f++)
                data[f] = (float)Math.Sin(2 * Math.PI * 997 * f / 44100);
            var r = Loudness.AnalyzeSamples(data, 1, 44100);
            Assert.InRange(r.IntegratedLufs, -3.4, -2.7);
        }

        [Fact]
        public void QuieterSine_GainRaisesToReference()
        {
            // 立体声幅度 0.1（-20dB）：z_sum = 2×(0.005×H²) → 约 -20 LUFS → 增益约 +2dB
            var r = Loudness.AnalyzeSamples(Sine(997, 0.1, 3.0), 2, 44100);
            Assert.InRange(r.IntegratedLufs, -21.0, -19.0);
            Assert.InRange(r.GainDb, 1.0, 3.0);
            Assert.Equal(0.1, r.Peak, 2);
        }

        [Fact]
        public void Silence_GainCappedAtMax()
        {
            var r = Loudness.AnalyzeSamples(new float[44100 * 2 * 2], 2, 44100);
            Assert.Equal(Loudness.MaxGainDb, r.GainDb);
            Assert.Equal(0, r.Peak);
        }

        [Fact]
        public void KWeight_AttenuatesInfrasonic()
        {
            // 同幅度下 20Hz（K 权重高通 38Hz 之外）响度应远低于 1kHz
            var low = Loudness.AnalyzeSamples(Sine(20, 1.0, 3.0), 2, 44100);
            var mid = Loudness.AnalyzeSamples(Sine(1000, 1.0, 3.0), 2, 44100);
            Assert.True(low.IntegratedLufs < mid.IntegratedLufs - 10,
                $"20Hz={low.IntegratedLufs:0.0} 1kHz={mid.IntegratedLufs:0.0}");
        }

        [Fact]
        public void ShortInput_DoesNotThrow()
        {
            var r = Loudness.AnalyzeSamples(new float[] { 0.5f, -0.5f, 0.5f, -0.5f }, 2, 44100);
            Assert.NotNull(r);
        }

        [Fact]
        public void LinearFor_ClampsByPeak()
        {
            // +12dB（≈3.98）但峰值为 1.0 → 钳到 1.0 防削波
            Assert.Equal(1.0, Loudness.LinearFor(12, 1.0), 3);
            // 峰值 0.5 → 最多放大 2 倍
            Assert.Equal(2.0, Loudness.LinearFor(12, 0.5), 3);
            // 常规增益不受影响：+5dB ≈ 1.78
            Assert.Equal(1.778, Loudness.LinearFor(5, 0.2), 2);
        }

        [Fact]
        public void AnalyzeFile_JunkAudio_ReturnsNullOrResult()
        {
            // 垃圾字节：要么解码失败返回 null，要么成功——不允许抛异常
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aurora_rg_" + Guid.NewGuid().ToString("N") + ".mp3");
            try
            {
                System.IO.File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4 });
                var r = Loudness.AnalyzeFile(p);
                if (r != null) Assert.True(r.Peak >= 0);
            }
            finally { System.IO.File.Delete(p); }
        }
    }

    public class GainFadeTests
    {
        /// <summary>恒定值假输入源。</summary>
        class ConstantSource : ISampleProvider
        {
            public float Value = 1f;
            public long RemainingFrames = 100000;
            public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); } }
            public int Read(float[] buffer, int offset, int count)
            {
                int frames = Math.Min(count / 2, (int)Math.Min(RemainingFrames, int.MaxValue));
                for (int f = 0; f < frames; f++)
                {
                    buffer[offset + f * 2] = Value;
                    buffer[offset + f * 2 + 1] = Value;
                }
                RemainingFrames -= frames;
                return frames * 2;
            }
        }

        [Fact]
        public void NoFade_Passthrough()
        {
            var src = new ConstantSource();
            var g = new GainFadeSampleProvider(src);
            var buf = new float[480];
            int n = g.Read(buf, 0, 480);
            Assert.Equal(480, n);
            Assert.All(buf, v => Assert.Equal(1f, v));
        }

        [Fact]
        public void BaseGain_Scales()
        {
            var g = new GainFadeSampleProvider(new ConstantSource()) { BaseGain = 0.5f };
            var buf = new float[480];
            g.Read(buf, 0, 480);
            Assert.All(buf, v => Assert.Equal(0.5f, v, 4));
        }

        [Fact]
        public void FadeIn_RampsLinearly()
        {
            var g = new GainFadeSampleProvider(new ConstantSource());
            g.BeginFadeIn(1.0);   // 1 秒淡入（48000Hz）
            var buf = new float[96000];   // 1 秒
            g.Read(buf, 0, buf.Length);
            // t=0.25s → 增益 0.25；t=0.5 → 0.5；t=0.99 → ≈1
            Assert.Equal(0.25f, buf[(int)(0.25 * 48000) * 2], 2);
            Assert.Equal(0.5f, buf[(int)(0.5 * 48000) * 2], 2);
            Assert.InRange(buf[(int)(0.99 * 48000) * 2], 0.95f, 1.0f);
        }

        [Fact]
        public void FadeOut_ArmedAfterDelay()
        {
            var g = new GainFadeSampleProvider(new ConstantSource());
            g.BeginFadeOut(1.0, 1.0);   // 1 秒后开始淡出，持续 1 秒
            var buf = new float[96000 * 2];   // 2 秒
            g.Read(buf, 0, buf.Length);
            // t=0.5s → 全量；t=1.5s → 0.5；t=2.0s → 0
            Assert.Equal(1f, buf[(int)(0.5 * 48000) * 2], 3);
            Assert.Equal(0.5f, buf[(int)(1.5 * 48000) * 2], 2);
            Assert.Equal(0f, buf[95999 * 2], 3);
        }

        [Fact]
        public void BaseGainChange_MidStream_Applies()
        {
            var g = new GainFadeSampleProvider(new ConstantSource());
            var buf = new float[480];
            g.Read(buf, 0, 480);
            g.BaseGain = 0.25f;
            g.Read(buf, 0, 480);
            Assert.All(buf, v => Assert.Equal(0.25f, v, 4));
        }

        [Fact]
        public void FadeInAndOut_Combine()
        {
            var g = new GainFadeSampleProvider(new ConstantSource());
            g.BeginFadeIn(2.0);
            g.BeginFadeOut(0, 1.0);   // 立即开始 1 秒淡出
            var buf = new float[96000];
            g.Read(buf, 0, buf.Length);
            // t=0.5s：淡入 0.25 × 淡出 0.5 → 0.125
            Assert.Equal(0.125f, buf[(int)(0.5 * 48000) * 2], 2);
        }
    }
}
