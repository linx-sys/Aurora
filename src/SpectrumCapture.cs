/* ============================================================
 * SpectrumCapture.cs — 频谱采样包装器（阶段 4 自 PlayerEngine 拆出）
 * 在播放链中插入，复制 PCM 数据给频谱模块。
 * 实现 ISampleProvider，不干扰播放线程。挂在混音器输出（48k stereo）。
 * UI 经 PlayerEngine.PullSpectrum() 主动拉取（约 30fps）。
 * ============================================================ */
using System;
using NAudio.Wave;

namespace Aurora
{
    internal class SampleCaptureProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[]? _lastBuffer;
        private int _lastCount;
        private readonly object _lock = new object();

        public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

        public SampleCaptureProvider(ISampleProvider source)
        {
            _source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read > 0)
            {
                lock (_lock)
                {
                    if (_lastBuffer == null || _lastBuffer.Length < read)
                        _lastBuffer = new float[read];
                    Array.Copy(buffer, offset, _lastBuffer, 0, read);
                    _lastCount = read;
                }
            }
            return read;
        }

        /// <summary>获取最近一帧的样本数据（线程安全复制）。</summary>
        public float[]? GetLastSamples()
        {
            lock (_lock)
            {
                if (_lastCount <= 0) return null;
                float[] result = new float[_lastCount];
                Array.Copy(_lastBuffer!, result, _lastCount);
                return result;
            }
        }
    }
}
