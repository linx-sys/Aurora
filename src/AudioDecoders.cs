/* ============================================================
 * AudioDecoders.cs — 解码器工厂 + 混音器格式归一化
 * P2 播放管线重构：PlayerEngine 与 Loudness 共用。
 * ============================================================ */
using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Aurora
{
    public static class AudioDecoders
    {
        /// <summary>
        /// 按扩展名打开解码器（调用方负责 Dispose）。
        /// OGG/OPUS 必须自解码（Media Foundation 不支持裸 .opus 字节流 0xC00D36C4、
        /// NVorbis 仅支持 Vorbis 编码）；其余交给 AudioFileReader（MF 解码）。
        /// 打开失败抛异常。
        /// </summary>
        public static WaveStream Open(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isVorbis = ext == ".ogg" || ext == ".oga";
            bool isOpus = ext == ".opus";
            if (isVorbis) return new VorbisWaveReader(path);
            if (isOpus) return new OpusWaveReader(path);
            return new AudioFileReader(path);
        }

        /// <summary>
        /// 归一化到混音器格式（48kHz 立体声 float）：
        /// 多声道取前二（5.1 等罕见格式，避免复杂下混矩阵）、单声道→立体声、
        /// 采样率 ≠ 48k 时 WDL 重采样。
        /// </summary>
        public static ISampleProvider NormalizeToMixer(ISampleProvider source)
        {
            if (source.WaveFormat.Channels > 2)
                source = new TruncateToStereoSampleProvider(source);
            if (source.WaveFormat.Channels == 1)
                source = new MonoToStereoSampleProvider(source);
            if (source.WaveFormat.SampleRate != MixerSampleRate)
                source = new WdlResamplingSampleProvider(source, MixerSampleRate);
            return source;
        }

        /// <summary>混音器目标格式。</summary>
        public static WaveFormat MixerFormat
        {
            get { return WaveFormat.CreateIeeeFloatWaveFormat(MixerSampleRate, MixerChannels); }
        }

        public const int MixerSampleRate = 48000;
        public const int MixerChannels = 2;
    }

    /// <summary>多声道（>2）取前两声道（FL/FR），其余丢弃——简单可预期，不做下混矩阵。</summary>
    class TruncateToStereoSampleProvider : ISampleProvider
    {
        readonly ISampleProvider _source;
        readonly int _srcChannels;
        float[] _srcBuf;

        public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(_source.WaveFormat.SampleRate, 2); } }

        public TruncateToStereoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _srcChannels = source.WaveFormat.Channels;
            _srcBuf = new float[2048 * _srcChannels];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _srcChannels;
            if (_srcBuf.Length < need) _srcBuf = new float[need];
            int read = _source.Read(_srcBuf, 0, need);
            int srcFrames = read / _srcChannels;
            for (int f = 0; f < srcFrames; f++)
            {
                buffer[offset + f * 2] = _srcBuf[f * _srcChannels];
                buffer[offset + f * 2 + 1] = _srcChannels > 1 ? _srcBuf[f * _srcChannels + 1] : _srcBuf[f * _srcChannels];
            }
            return srcFrames * 2;
        }
    }
}
