/* ============================================================
 * VorbisWaveReader.cs — NVorbis 流式读取器（NAudio WaveStream）
 * 直接将 OGG/OPUS 解码流接入播放链，无需先解码为临时 WAV。
 * 实现 WaveStream + ISampleProvider，可直接替换 AudioFileReader。
 * ============================================================ */

using System;
using System.IO;
using NAudio.Wave;
using NVorbis;

namespace Aurora
{
    /// <summary>
    /// 将 NVorbis.VorbisReader 包装为 NAudio WaveStream + ISampleProvider。
    /// 流式读取 OGG/OPUS，消除磁盘 I/O、切换延迟与临时文件泄漏。
    /// </summary>
    public class VorbisWaveReader : WaveStream, ISampleProvider
    {
        private readonly VorbisReader _reader;
        private readonly WaveFormat _waveFormat;
        private long _position; // 字节位置（用于 WaveStream.Position）
        private bool _disposed;

        public VorbisWaveReader(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("OGG file not found", path);

            _reader = new VorbisReader(path);
            // IEEE float 32-bit，与 NAudio SampleChain 兼容
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_reader.SampleRate, _reader.Channels);
        }

        public override WaveFormat WaveFormat => _waveFormat;

        /// <summary>总长度（字节）。IEEE float = 4 bytes/sample/channel。</summary>
        public override long Length =>
            (long)(_reader.TotalTime.TotalSeconds * _reader.SampleRate * _reader.Channels * 4);

        /// <summary>当前位置（字节）。</summary>
        public override long Position
        {
            get => _position;
            set
            {
                // 字节 → 样本数 → 时间
                long sampleOffset = value / (4 * _reader.Channels);
                _reader.TimePosition = TimeSpan.FromSeconds((double)sampleOffset / _reader.SampleRate);
                _position = value;
            }
        }

        /// <summary>读取 PCM 字节（IEEE float）。WaveStream 接口。</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) return 0;

            // 字节 → float 样本数
            int floatCount = count / 4;
            var floatBuffer = new float[floatCount];
            int samplesRead = _reader.ReadSamples(floatBuffer, 0, floatCount);

            // float → byte（Buffer.BlockCopy 不做字节序转换，小端系统直接复制）
            int bytesRead = samplesRead * 4;
            Buffer.BlockCopy(floatBuffer, 0, buffer, offset, bytesRead);
            _position += bytesRead;
            return bytesRead;
        }

        /// <summary>读取 float 样本。ISampleProvider 接口（频谱/播放链优先使用）。</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            if (_disposed) return 0;
            int samplesRead = _reader.ReadSamples(buffer, offset, count);
            _position += samplesRead * 4;
            return samplesRead;
        }

        // WaveStream 已提供 CurrentTime / TotalTime（TimeSpan 类型，基于 Position/Length 计算），
        // 此处不再重复定义，避免隐藏基类属性导致 PlayerEngine 类型不匹配。

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _reader?.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
