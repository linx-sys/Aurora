/* ============================================================
 * GainFadeSampleProvider.cs — 曲目输入的增益与淡入淡出包络
 * P2：ReplayGain 增益（BaseGain，外部随时可调）× 淡入/淡出包络。
 * 包络在 Read 内按已读秒数计算（确定性、无定时器、线程安全），
 * 淡出支持"从现在起 afterSeconds 后开始，持续 fadeSeconds"。
 * ============================================================ */
using System;
using NAudio.Wave;

namespace Aurora
{
    public class GainFadeSampleProvider : ISampleProvider
    {
        readonly ISampleProvider _source;
        readonly object _lock = new object();
        double _readSeconds;          // 本输入已读秒数
        float _baseGain = 1f;         // ReplayGain 线性增益
        double _fadeInSec;            // 淡入总时长（0=无）
        double _fadeOutStartAt = double.MaxValue;  // 开始淡出的秒点
        double _fadeOutSec;           // 淡出时长

        public GainFadeSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

        /// <summary>基础增益（ReplayGain 线性值；1=不变）。实时生效。</summary>
        public float BaseGain
        {
            get { lock (_lock) { return _baseGain; } }
            set { lock (_lock) { _baseGain = Math.Max(0f, Math.Min(8f, value)); } }
        }

        /// <summary>从输入起点开始淡入（时长秒）。</summary>
        public void BeginFadeIn(double seconds)
        {
            lock (_lock)
            {
                _fadeInSec = Math.Max(0, seconds);
            }
        }

        /// <summary>从现在起 afterSeconds 秒后开始淡出，持续 fadeSeconds 秒。</summary>
        public void BeginFadeOut(double afterSeconds, double fadeSeconds)
        {
            lock (_lock)
            {
                _fadeOutStartAt = _readSeconds + Math.Max(0, afterSeconds);
                _fadeOutSec = Math.Max(0.05, fadeSeconds);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read == 0) return 0;

            lock (_lock)
            {
                bool needsEnvelope = _fadeInSec > 0 || _fadeOutStartAt != double.MaxValue || _baseGain != 1f;
                if (!needsEnvelope) return read;

                int channels = _source.WaveFormat.Channels;
                int frames = read / channels;
                double sr = _source.WaveFormat.SampleRate;
                double pos = _readSeconds;

                for (int f = 0; f < frames; f++)
                {
                    double t = pos + f / sr;
                    double gain = _baseGain;

                    if (_fadeInSec > 0)
                    {
                        double fi = Math.Min(1.0, t / _fadeInSec);
                        gain *= fi;
                    }
                    if (t >= _fadeOutStartAt)
                    {
                        double fo = (t - _fadeOutStartAt) / _fadeOutSec;
                        gain *= Math.Max(0.0, 1.0 - fo);
                    }

                    if (gain != 1.0)
                    {
                        int baseIdx = offset + f * channels;
                        for (int c = 0; c < channels; c++)
                            buffer[baseIdx + c] *= (float)gain;
                    }
                }
                _readSeconds += frames / sr;
            }
            return read;
        }
    }
}
