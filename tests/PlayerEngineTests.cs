/* ============================================================
 * PlayerEngineTests.cs — 音频引擎单元测试（P1-4，fake 输出注入）
 * FakeAudioOutput 用后台线程模拟真实设备的"拉模型"取数：
 * 播放时持续从混音器读样本，输入耗尽 → MixerInputEnded → 自然结束路径，
 * 全程不碰真实声卡，CI 可跑。
 * FakeWaveStream 模拟 48kHz float 解码流（可定位、有限时长）。
 * ============================================================ */
using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Xunit;

namespace Aurora.Tests
{
    /// <summary>模拟解码流：48kHz stereo float，可 Seek，有限时长；amplitude>0 时输出非静音样本（ReplayGain 联动测试用）。</summary>
    class FakeWaveStream : WaveStream
    {
        readonly WaveFormat fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        readonly float amplitude;
        long pos;

        public FakeWaveStream(double seconds, float amplitude = 0f)
        {
            TotalTimeSeconds = seconds;
            this.amplitude = amplitude;
        }

        public double TotalTimeSeconds { get; set; }

        public override WaveFormat WaveFormat { get { return fmt; } }
        public override long Length { get { return (long)(fmt.AverageBytesPerSecond * TotalTimeSeconds); } }
        public override long Position
        {
            get { return pos; }
            set { pos = Math.Max(0, Math.Min(Length, value)); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = Length - pos;
            int n = (int)Math.Min(count, remaining);
            if (amplitude > 0)
            {
                byte[] sample = BitConverter.GetBytes(amplitude);
                for (int i = 0; i + 4 <= n; i += 4)
                {
                    buffer[offset + i] = sample[0];
                    buffer[offset + i + 1] = sample[1];
                    buffer[offset + i + 2] = sample[2];
                    buffer[offset + i + 3] = sample[3];
                }
            }
            pos += n;
            return n;
        }
    }

    /// <summary>模拟输出设备：Play 时后台线程从混音器拉样本（真实设备的行为模型）；记录峰值供增益联动断言。</summary>
    class FakeAudioOutput : IAudioOutput
    {
        public ISampleProvider? Source;
        public int PlayCount, PauseCount, StopCount;
        public float Peak;                       // 已拉取样本的最大绝对幅值
        readonly object peakLock = new object();
        public float Volume { get; set; } = 1f;
        Thread? puller;
        volatile bool pulling;

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Play()
        {
            PlayCount++;
            pulling = true;
            puller = new Thread(PullLoop) { IsBackground = true };
            puller.Start();
        }

        public void ResetPeak() { lock (peakLock) { Peak = 0; } }

        public float GetPeak() { lock (peakLock) { return Peak; } }

        void PullLoop()
        {
            var buf = new float[4096];
            while (pulling)
            {
                int n;
                try { n = Source != null ? Source.Read(buf, 0, buf.Length) : 0; }
                catch { break; }
                if (n <= 0) break;   // 混音器无输入可读 → 触发 MixerInputEnded（自然结束）
                lock (peakLock)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float a = Math.Abs(buf[i]);
                        if (a > Peak) Peak = a;
                    }
                }
                Thread.Sleep(1);
            }
        }

        public void Pause() { PauseCount++; pulling = false; }
        public void Stop()
        {
            StopCount++;
            pulling = false;
            var handler = PlaybackStopped;
            if (handler != null) handler(this, new StoppedEventArgs());
        }
        public void Dispose() { pulling = false; }
    }

    public class PlayerEngineTests : IDisposable
    {
        readonly string dummyFile = Path.Combine(Path.GetTempPath(), "aurora_engine_" + Guid.NewGuid().ToString("N") + ".mp3");
        readonly FakeAudioOutput fakeOut = new FakeAudioOutput();
        readonly PlayerEngine engine;

        /// <summary>构造注入：fake 输出 + fake 解码器（1 秒流）。</summary>
        public PlayerEngineTests()
        {
            File.WriteAllText(dummyFile, "dummy");
            engine = new PlayerEngine(
                src => { fakeOut.Source = src; return fakeOut; },
                p => new FakeWaveStream(1.0));
        }

        public void Dispose()
        {
            try { engine.Dispose(); } catch { }
            try { File.Delete(dummyFile); } catch { }
        }

        // ---------- 加载 ----------

        [Fact]
        public void Load_DecoderThrows_ReturnsFalse()
        {
            var failing = new PlayerEngine(
                src => new FakeAudioOutput { Source = src },
                p => throw new InvalidDataException("fake decode fail"));
            try
            {
                Assert.False(failing.Load(dummyFile));
                Assert.Equal(PlaybackState.Stopped, failing.State);
            }
            finally { failing.Dispose(); }
        }

        [Fact]
        public void Load_MissingFile_ReturnsFalse()
        {
            Assert.False(engine.Load(dummyFile + ".not-exist"));
        }

        [Fact]
        public void Load_Valid_SessionIncrementsPerLoad()
        {
            Assert.True(engine.Load(dummyFile));
            long s1 = engine.SessionId;
            Assert.Equal(PlaybackState.Stopped, engine.State);
            Assert.Equal(dummyFile, engine.CurrentPath);

            Assert.True(engine.Load(dummyFile));
            Assert.Equal(s1 + 1, engine.SessionId);
        }

        [Fact]
        public void Load_Valid_DurationFromReader()
        {
            engine.Load(dummyFile);
            Assert.Equal(TimeSpan.FromSeconds(1.0), engine.Duration);
        }

        // ---------- 播放 / 暂停 / 停止 状态机 ----------

        [Fact]
        public void PlayPause_StateMachine()
        {
            engine.Load(dummyFile);
            engine.Play();
            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(1, fakeOut.PlayCount);

            engine.Pause();
            Assert.Equal(PlaybackState.Paused, engine.State);
            Assert.Equal(1, fakeOut.PauseCount);

            engine.Play();
            Assert.Equal(PlaybackState.Playing, engine.State);
        }

        [Fact]
        public void Stop_ResetsStateAndClearsDuration()
        {
            engine.Load(dummyFile);
            engine.Play();
            engine.Stop();

            Assert.Equal(PlaybackState.Stopped, engine.State);
            Assert.Equal(TimeSpan.Zero, engine.Duration);   // 当前输入已移除
            Assert.Equal(1, fakeOut.StopCount);
        }

        // ---------- 进度跳转 ----------

        [Fact]
        public void Seek_ClampsNegativeToZero()
        {
            engine.Load(dummyFile);
            engine.Seek(TimeSpan.FromSeconds(-5));
            Assert.Equal(TimeSpan.Zero, engine.Position);
        }

        [Fact]
        public void Seek_ClampsBeyondDuration()
        {
            engine.Load(dummyFile);
            engine.Seek(TimeSpan.FromSeconds(99));
            Assert.Equal(TimeSpan.FromSeconds(1.0), engine.Position);
        }

        // ---------- 音量 ----------

        [Fact]
        public void Volume_ClampedToUnitRange()
        {
            engine.Volume = 5f;
            Assert.Equal(1f, engine.Volume);
            engine.Volume = -1f;
            Assert.Equal(0f, engine.Volume);
        }

        // ---------- 自然结束 / 会话 ----------

        [Fact]
        public void NaturalEnd_FiresPlaybackEndedWithSession()
        {
            var ended = new ManualResetEventSlim(false);
            PlaybackEndedEventArgs? args = null;
            engine.PlaybackEnded += (s, e) => { args = e; ended.Set(); };

            engine.Load(dummyFile);
            long session = engine.SessionId;
            engine.Play();

            // fake 设备拉完 1 秒数据 → 混音器输入耗尽 → 自然结束
            Assert.True(ended.Wait(TimeSpan.FromSeconds(10)), "未在超时内收到 PlaybackEnded");
            Assert.NotNull(args);
            Assert.Equal(session, args.SessionId);
            Assert.Equal(dummyFile, args.Path);
            Assert.Equal(PlaybackState.Stopped, engine.State);
        }

        // ---------- 跨淡 ----------

        [Fact]
        public void CrossfadeTo_MissingFile_ReturnsFalse()
        {
            engine.Load(dummyFile);
            Assert.False(engine.CrossfadeTo(dummyFile + ".not-exist", 2.0));
        }

        [Fact]
        public void CrossfadeTo_Valid_SwitchesSessionAndPlays()
        {
            engine.Load(dummyFile);
            long s1 = engine.SessionId;
            Assert.True(engine.CrossfadeTo(dummyFile, 0.05));
            Assert.Equal(s1 + 1, engine.SessionId);
            Assert.Equal(PlaybackState.Playing, engine.State);   // 跨淡即播
        }
    }
}
