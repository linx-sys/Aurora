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
using System.Collections.Generic;
using System.Threading;
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

        /// <summary>分析单声道或立体声交错 PCM；与文件分析使用相同的连续滤波路径。</summary>
        public static Result AnalyzeSamples(float[] interleaved, int channels, int sampleRate)
        {
            if (channels <= 0 || sampleRate <= 0 || interleaved == null || interleaved.Length == 0)
                return new Result { IntegratedLufs = -70, GainDb = MaxGainDb, Peak = 0 };
            var analyzer = new StreamingAnalyzer(channels, sampleRate);
            analyzer.Add(interleaved, interleaved.Length, CancellationToken.None);
            return analyzer.Complete(CancellationToken.None);
        }

        /// <summary>流式分析，不取得输入源的所有权；读取块大小不会改变滤波状态或测量窗口。</summary>
        public static Result AnalyzeStream(ISampleProvider source, CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            cancellationToken.ThrowIfCancellationRequested();
            var analyzer = new StreamingAnalyzer(source.WaveFormat.Channels, source.WaveFormat.SampleRate);
            var buffer = new float[4096 * source.WaveFormat.Channels];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0) break;
                analyzer.Add(buffer, read, cancellationToken);
            }
            return analyzer.Complete(cancellationToken);
        }

        // 仅保留 400ms 帧能量环和每 100ms 一个块能量；不保留整首 PCM。
        sealed class StreamingAnalyzer
        {
            readonly int _channels;
            readonly int _hopFrames;
            readonly Biquad[] _shelf;
            readonly Biquad[] _hp;
            readonly double[] _window;
            readonly List<double> _blocks = new List<double>();
            int _channel, _windowPos;
            long _frames;
            double _frameEnergy, _windowEnergy, _peak;

            public StreamingAnalyzer(int channels, int sampleRate)
            {
                if (channels < 1 || channels > 2)
                    throw new NotSupportedException("响度分析仅支持已知的单声道或立体声布局，不推测多声道顺序。");
                if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
                _channels = channels;
                _hopFrames = Math.Max(1, (int)(0.1 * sampleRate));
                _window = new double[Math.Max(1, (int)(0.4 * sampleRate))];
                _shelf = new Biquad[channels];
                _hp = new Biquad[channels];
                for (int c = 0; c < channels; c++)
                {
                    _shelf[c] = DesignShelf(sampleRate);
                    _hp[c] = DesignHighPass(sampleRate);
                }
            }

            public void Add(float[] samples, int count, CancellationToken token)
            {
                for (int i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    double sample = samples[i];
                    if (!double.IsFinite(sample)) throw new InvalidDataException("音频包含非有限样本");
                    _peak = Math.Max(_peak, Math.Abs(sample));
                    double weighted = _hp[_channel].Process(_shelf[_channel].Process(sample));
                    _frameEnergy += weighted * weighted;
                    if (++_channel != _channels) continue;
                    _channel = 0;
                    _windowEnergy += _frameEnergy - _window[_windowPos];
                    _window[_windowPos] = _frameEnergy;
                    _windowPos = (_windowPos + 1) % _window.Length;
                    _frameEnergy = 0;
                    _frames++;
                    if (_frames >= _window.Length && (_frames - _window.Length) % _hopFrames == 0)
                        _blocks.Add(Math.Max(0, _windowEnergy) / _window.Length);
                }
            }

            public Result Complete(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (_channel != 0) throw new InvalidDataException("音频以不完整声道帧结束");
                // 不足 400ms 时保持短片段单块分析的兼容语义。
                if (_blocks.Count == 0 && _frames > 0) _blocks.Add(_windowEnergy / _frames);
                double absoluteGate = Math.Pow(10, (-70 + 0.691) / 10);
                double sum = 0;
                int count = 0;
                foreach (double energy in _blocks)
                {
                    token.ThrowIfCancellationRequested();
                    if (energy > absoluteGate) { sum += energy; count++; }
                }
                double integrated = -70;
                if (count > 0)
                {
                    double relativeGate = sum / count * 0.1;
                    sum = 0;
                    count = 0;
                    foreach (double energy in _blocks)
                    {
                        token.ThrowIfCancellationRequested();
                        if (energy > absoluteGate && energy > relativeGate) { sum += energy; count++; }
                    }
                    if (count > 0) integrated = -0.691 + 10 * Math.Log10(sum / count);
                }
                return new Result
                {
                    IntegratedLufs = integrated,
                    GainDb = Math.Clamp(ReferenceLufs - integrated, -40, MaxGainDb),
                    Peak = _peak
                };
            }
        }

        /// <summary>后台流式解码分析；解码失败返回 null，取消向调用方传播。</summary>
        public static Result? AnalyzeFile(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (WaveStream reader = AudioDecoders.Open(path))
                    return AnalyzeStream(reader.ToSampleProvider(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
            if (!double.IsFinite(gainDb) || !double.IsFinite(peak) || peak < 0) return 1.0;
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
