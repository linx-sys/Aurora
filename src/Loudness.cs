/* ============================================================
 * Loudness.cs — ReplayGain 2.0 / EBU R128 响度分析
 * P2：K-weighting 前置滤波（RBJ 双二阶：高架 1681.97Hz +3.999dB Q0.7071
 * + 高通 38.135Hz Q0.5003）→ 400ms 块 / 100ms 步的门限响度
 * （绝对门 -70 LUFS，相对门 -10 LU）→ 综合响度 LUFS。
 * 增益 = -18 LUFS（RG2.0 参考）- 综合响度；峰值另记用于防削波钳制。
 *
 * 参考校准：997Hz 满幅正弦 ≈ -3.0 LUFS（ITU BS.1770）。
 * ============================================================ */
using System;
using System.IO;
using NAudio.Wave;

namespace Aurora
{
    public static class Loudness
    {
        public const double ReferenceLufs = -18.0;   // ReplayGain 2.0 参考
        public const double MaxGainDb = 12.0;        // 增益上限（防极端放大）

        public class Result
        {
            public double IntegratedLufs;   // 综合响度
            public double GainDb;           // 回放增益（dB）
            public double Peak;             // 采样峰值（0~1）
        }

        /* ---------------- 双二阶滤波器 ---------------- */

        struct Biquad
        {
            public double b0, b1, b2, a1, a2;
            public double x1, x2, y1, y2;

            public double Process(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                return y;
            }
        }

        // K-weighting 级 1：高架（high-shelf），f0=1681.97Hz，G=+3.9998dB，Q=0.7071
        static Biquad DesignShelf(double sampleRate)
        {
            double f0 = 1681.9744509555319, G = 3.999843853973347, Q = 0.7071752369554196;
            double A = Math.Pow(10, G / 40.0);
            double w0 = 2 * Math.PI * f0 / sampleRate;
            double alpha = Math.Sin(w0) / (2 * Q);
            double cw = Math.Cos(w0);
            double sqA = 2 * Math.Sqrt(A) * alpha;

            // RBJ high-shelf
            double b0 = A * ((A + 1) + (A - 1) * cw + sqA);
            double b1 = -2 * A * ((A - 1) + (A + 1) * cw);
            double b2 = A * ((A + 1) + (A - 1) * cw - sqA);
            double a0 = (A + 1) - (A - 1) * cw + sqA;
            double a1 = 2 * ((A - 1) - (A + 1) * cw);
            double a2 = (A + 1) - (A - 1) * cw - sqA;

            Biquad b;
            b.b0 = b0 / a0; b.b1 = b1 / a0; b.b2 = b2 / a0;
            b.a1 = a1 / a0; b.a2 = a2 / a0;
            b.x1 = b.x2 = b.y1 = b.y2 = 0;
            return b;
        }

        // K-weighting 级 2：高通，f0=38.135Hz，Q=0.5003
        static Biquad DesignHighPass(double sampleRate)
        {
            double f0 = 38.13547087602444, Q = 0.5003270373238773;
            double w0 = 2 * Math.PI * f0 / sampleRate;
            double alpha = Math.Sin(w0) / (2 * Q);
            double cw = Math.Cos(w0);

            double b0 = (1 + cw) / 2;
            double b1 = -(1 + cw);
            double b2 = (1 + cw) / 2;
            double a0 = 1 + alpha;
            double a1 = -2 * cw;
            double a2 = 1 - alpha;

            Biquad b;
            b.b0 = b0 / a0; b.b1 = b1 / a0; b.b2 = b2 / a0;
            b.a1 = a1 / a0; b.a2 = a2 / a0;
            b.x1 = b.x2 = b.y1 = b.y2 = 0;
            return b;
        }

        /* ---------------- 分析 ---------------- */

        /// <summary>
        /// 分析交错 PCM 样本的门限响度（channels≤2 时权重 1.0）。
        /// 返回综合响度 LUFS；无有效块返回 -70。
        /// </summary>
        public static Result AnalyzeSamples(float[] interleaved, int channels, int sampleRate)
        {
            if (channels <= 0 || sampleRate <= 0 || interleaved == null || interleaved.Length == 0)
                return new Result { IntegratedLufs = -70, GainDb = MaxGainDb, Peak = 0 };

            int ch = Math.Min(channels, 2);
            var shelf = new Biquad[ch];
            var hp = new Biquad[ch];
            for (int c = 0; c < ch; c++) { shelf[c] = DesignShelf(sampleRate); hp[c] = DesignHighPass(sampleRate); }

            // 块参数：400ms 块，100ms 步
            int blockSamples = (int)(0.4 * sampleRate) * ch;
            int hopSamples = (int)(0.1 * sampleRate) * ch;
            if (blockSamples == 0 || interleaved.Length < blockSamples)
            {
                // 太短：退化为单块（不足 400ms 按整段算）
                blockSamples = interleaved.Length;
                hopSamples = interleaved.Length;
            }

            // 预滤波整段 + 累计峰值（K 滤波前后峰值差异：RG 用未加权峰值，这里单独扫原始样本）
            double peak = 0;
            for (int i = 0; i < interleaved.Length; i++)
            {
                double a = Math.Abs(interleaved[i]);
                if (a > peak) peak = a;
            }

            var blockLoudness = new System.Collections.Generic.List<double>();
            int offset = 0;
            while (offset + blockSamples <= interleaved.Length)
            {
                // 每块重置滤波器状态（BS.1770 分块独立测量）
                for (int c = 0; c < ch; c++) { shelf[c] = DesignShelf(sampleRate); hp[c] = DesignHighPass(sampleRate); }

                double sumSq = 0;
                int frames = blockSamples / ch;
                for (int f = 0; f < frames; f++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        double x = interleaved[offset + f * ch + c];
                        double y = hp[c].Process(shelf[c].Process(x));
                        sumSq += y * y;   // 立体声权重各 1.0
                    }
                }
                double meanSquare = sumSq / frames;   // 每声道均方（权重已含 1.0）
                double lufs = -0.691 + 10 * Math.Log10(meanSquare + 1e-24);
                blockLoudness.Add(lufs);

                offset += hopSamples;
            }

            // 两阶段门限
            double sum = 0; int count = 0;
            foreach (double l in blockLoudness)
            {
                if (l > -70.0) { sum += Math.Pow(10, l / 10); count++; }
            }
            double integrated = -70;
            if (count > 0)
            {
                double absGatedLufs = 10 * Math.Log10(sum / count);
                double relThreshold = absGatedLufs - 10.0;
                double sum2 = 0; int count2 = 0;
                foreach (double l in blockLoudness)
                {
                    if (l > -70.0 && l > relThreshold) { sum2 += Math.Pow(10, l / 10); count2++; }
                }
                if (count2 > 0) integrated = 10 * Math.Log10(sum2 / count2);
            }

            double gainDb = Math.Max(-40, Math.Min(MaxGainDb, ReferenceLufs - integrated));
            return new Result { IntegratedLufs = integrated, GainDb = gainDb, Peak = peak };
        }

        /// <summary>分析文件（全解码；后台线程调用）。失败返回 null。</summary>
        public static Result? AnalyzeFile(string path)
        {
            try
            {
                using (WaveStream reader = AudioDecoders.Open(path))
                {
                    ISampleProvider sp = reader.ToSampleProvider();
                    int ch = sp.WaveFormat.Channels;
                    int sr = sp.WaveFormat.SampleRate;
                    if (ch > 2) ch = 2;

                    var all = new System.Collections.Generic.List<float>(1 << 20);
                    var buf = new float[sr * ch];   // 1 秒缓冲
                    int n;
                    while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                    {
                        // 多声道取前二（与播放管线一致）
                        if (sp.WaveFormat.Channels <= 2)
                        {
                            for (int i = 0; i < n; i++) all.Add(buf[i]);
                        }
                        else
                        {
                            int frames = n / sp.WaveFormat.Channels;
                            for (int f = 0; f < frames; f++)
                            {
                                all.Add(buf[f * sp.WaveFormat.Channels]);
                                all.Add(buf[f * sp.WaveFormat.Channels + 1]);
                            }
                        }
                    }
                    return AnalyzeSamples(all.ToArray(), ch, sr);
                }
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("Loudness.AnalyzeFile FAIL: " + path + " -> " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// ReplayGain 线性增益（含防削波钳制）：
        /// 增益限制在 ±MaxGainDb，且保证 峰值×增益 ≤ 1.0。
        /// </summary>
        public static double LinearFor(double gainDb, double peak)
        {
            double db = Math.Max(-40, Math.Min(MaxGainDb, gainDb));
            double linear = Math.Pow(10, db / 20.0);
            if (peak > 0.0001)
            {
                double maxLinear = 1.0 / peak;
                if (linear > maxLinear) linear = maxLinear;
            }
            return Math.Max(0.0316, Math.Min(8.0, linear));   // -30dB ~ +18dB 硬限
        }
    }
}
