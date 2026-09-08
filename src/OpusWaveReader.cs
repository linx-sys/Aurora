/* ============================================================
 * OpusWaveReader.cs — Concentus 流式 OPUS 读取器（NAudio WaveStream）
 * 将 Concentus.Oggfile 解码流接入 NAudio 播放链，无需临时 WAV。
 * 实现 WaveStream + ISampleProvider，与 VorbisWaveReader 同构。
 * ============================================================ */

using System;
using System.Collections.Generic;
using System.IO;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace Aurora
{
    /// <summary>
    /// 将 Concentus Opus 解码包装为 NAudio WaveStream + ISampleProvider。
    /// 流式读取 .opus（Ogg Opus）文件，解码输出 48kHz IEEE float PCM。
    /// </summary>
    public class OpusWaveReader : WaveStream, ISampleProvider
    {
        private readonly FileStream _fileStream;
        private readonly OpusDecoder _decoder;
        private readonly OpusOggReadStream _reader;
        private readonly WaveFormat _waveFormat;
        private readonly int _channels;
        private readonly int _sampleRate = 48000;   // Opus 解码固定 48kHz
        private float[] _pending = new float[16384];
        private int _pendingCount;
        private long _position; // 字节位置（IEEE float = 4 bytes/样本）
        private bool _disposed;

        public OpusWaveReader(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Opus file not found", path);

            _fileStream = File.OpenRead(path);
            int channels = ReadOpusChannelCount(_fileStream);
            if (channels < 1 || channels > 2) channels = 2;   // Concentus 仅支持单/双声道

            _decoder = OpusDecoder.Create(_sampleRate, channels);
            _reader = new OpusOggReadStream(_decoder, _fileStream);
            _channels = channels;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, channels);
        }

        public override WaveFormat WaveFormat => _waveFormat;

        /// <summary>总长度（字节）。IEEE float = 4 bytes/sample/channel。</summary>
        public override long Length =>
            (long)(_reader.TotalTime.TotalSeconds * _sampleRate * _channels * 4);

        /// <summary>当前位置（字节）。</summary>
        public override long Position
        {
            get => _position;
            set
            {
                if (_disposed) return;
                long sampleOffset = value / (4L * _channels);
                _reader.SeekTo(TimeSpan.FromSeconds((double)sampleOffset / _sampleRate));
                _pendingCount = 0;   // 丢弃未消费的解码缓冲
                _position = value;
            }
        }

        /// <summary>读取 PCM 字节（IEEE float）。WaveStream 接口。</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            var floatBuffer = new float[floatCount];
            int samplesRead = Read(floatBuffer, 0, floatCount);
            int bytesRead = samplesRead * 4;
            Buffer.BlockCopy(floatBuffer, 0, buffer, offset, bytesRead);
            return bytesRead;
        }

        /// <summary>读取 float 样本。ISampleProvider 接口（播放链/频谱优先使用）。</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            if (_disposed) return 0;

            int totalCopied = 0;
            while (totalCopied < count)
            {
                if (_pendingCount == 0)
                {
                    if (!FillPending()) break;   // 解码下一包；失败/结束即停止
                }
                int n = Math.Min(count - totalCopied, _pendingCount);
                Array.Copy(_pending, 0, buffer, offset + totalCopied, n);
                ShiftPending(n);
                totalCopied += n;
                _position += (long)n * 4;
            }
            return totalCopied;
        }

        /// <summary>解码下一个 Opus 包到内部缓冲。返回 false 表示流结束或出错。</summary>
        private bool FillPending()
        {
            // HasNextPacket 为 false 表示流结束
            if (!_reader.HasNextPacket) return false;
            short[] pcm;
            try { pcm = _reader.DecodeNextPacket(); }
            catch { return false; }
            if (pcm == null || pcm.Length == 0) return false;

            if (_pending.Length < pcm.Length)
                _pending = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
                _pending[i] = pcm[i] / 32768f;   // short → float
            _pendingCount = pcm.Length;
            return true;
        }

        /// <summary>消费内部缓冲前 n 个样本（简单搬移，n 与缓冲规模同为千级，开销可忽略）。</summary>
        private void ShiftPending(int n)
        {
            int rest = _pendingCount - n;
            if (rest > 0)
                Array.Copy(_pending, n, _pending, 0, rest);
            _pendingCount = rest;
        }

        /// <summary>解析 Ogg 第一页 OpusHead 包中的声道数（偏移 9 字节）。</summary>
        static int ReadOpusChannelCount(FileStream fs)
        {
            try
            {
                fs.Position = 0;
                var pageHeader = new byte[27];
                if (fs.Read(pageHeader, 0, 27) < 27) return 2;
                if (pageHeader[0] != (byte)'O' || pageHeader[1] != (byte)'g' ||
                    pageHeader[2] != (byte)'g' || pageHeader[3] != (byte)'S') return 2;
                int segCount = pageHeader[26];
                var segTable = new byte[segCount];
                if (fs.Read(segTable, 0, segCount) < segCount) return 2;
                int firstPacketLen = 0;
                for (int i = 0; i < segCount; i++)
                {
                    firstPacketLen += segTable[i];
                    if (segTable[i] < 255) break;   // 255 表示包延续到下一 segment
                }
                if (firstPacketLen < 19) return 2;
                var opusHead = new byte[firstPacketLen];
                if (fs.Read(opusHead, 0, firstPacketLen) < firstPacketLen) return 2;
                if (opusHead[0] != (byte)'O' || opusHead[1] != (byte)'p' ||
                    opusHead[2] != (byte)'u' || opusHead[3] != (byte)'s' ||
                    opusHead[4] != (byte)'H') return 2;
                int ch = opusHead[9];
                return ch >= 1 ? ch : 2;
            }
            catch { return 2; }
            finally { fs.Position = 0; }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _fileStream?.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
