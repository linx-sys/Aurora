/* ============================================================
 * PlayerEngine.cs — 音频播放引擎（P2 管线重构）
 *
 * 架构：常驻输出设备
 *   WaveOutEvent（或 WASAPI Exclusive）
 *     ← SampleCaptureProvider（频谱采样，混音器输出）
 *     ← MixingSampleProvider（48kHz 立体声 float，ReadFully=false）
 *        ← 每曲目输入：解码器 → 格式归一化 → GainFadeSampleProvider
 *
 * 关键收益：
 * 1. 设备只建一次 → 切歌不再重建 WaveOut，"PlaybackStopped 竞态"的
 *    根因消失（原 300ms/200ms/retry×3 经验阈值 workaround 删除）；
 * 2. 曲目自然结束由 MixerInputEnded 判定并携带会话 ID，过期事件丢弃；
 * 3. 预加载下一首解码器（Preload）+ CrossfadeTo 跨淡切换 → 无缝衔接；
 * 4. ReplayGain 增益作用于每输入（SetReplayGain）。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using NAudio.Wave.SampleProviders;

namespace Aurora
{
    /// <summary>播放状态。</summary>
    public enum PlaybackState { Stopped, Playing, Paused }

    /// <summary>播放状态变更事件参数。</summary>
    public class PlaybackStateChangedEventArgs : EventArgs
    {
        public PlaybackState State;
        public TimeSpan Position;
        public TimeSpan Duration;
    }

    /// <summary>频谱数据就绪事件参数。</summary>
    public class SpectrumDataEventArgs : EventArgs
    {
        public float[] Samples;   // 当前播放帧的 PCM 浮点样本（交错）
        public int Channels;
        public int SampleRate;
    }

    /// <summary>播放自然结束事件参数（携带播放会话 ID，供订阅方过滤过期事件）。</summary>
    public class PlaybackEndedEventArgs : EventArgs
    {
        public long SessionId;
        public string Path;
    }

    /// <summary>
    /// 样本抓取包装器：在播放链中插入，复制 PCM 数据给频谱模块。
    /// 实现 ISampleProvider，不干扰播放线程。挂在混音器输出（48k stereo）。
    /// </summary>
    internal class SampleCaptureProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _lastBuffer;
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
        public float[] GetLastSamples()
        {
            lock (_lock)
            {
                if (_lastCount <= 0) return null;
                float[] result = new float[_lastCount];
                Array.Copy(_lastBuffer, result, _lastCount);
                return result;
            }
        }
    }

    /// <summary>
    /// 音频播放引擎。常驻混音器架构，支持预加载、跨淡切换与 ReplayGain。
    /// </summary>
    public class PlayerEngine : IDisposable
    {
        /// <summary>混音器中的一条曲目输入。</summary>
        class TrackInput
        {
            public long SessionId;
            public string Path;
            public WaveStream Reader;            // 源格式（Position/TotalTime）
            public GainFadeSampleProvider Gain;  // 归一化后的混音器输入
            public bool InMixer;
            public bool ReaderDisposed;          // Reader 已释放（防 ObjectDisposedException）
        }

        IWavePlayer _output;
        SampleCaptureProvider _capture;      // 频谱采样（挂在混音器输出）
        MixingSampleProvider _mixer;
        readonly object _lock = new object();

        readonly List<TrackInput> _inputs = new List<TrackInput>();  // 活跃输入
        TrackInput _current;

        long _sessionId;
        string _currentPath;
        bool _disposed;
        bool _deviceStopping;                // 主动 Stop 时忽略设备 PlaybackStopped
        float _desiredVolume = 1.0f;         // 主音量（WaveOut.Volume，持久保持）
        float _rgLinear = 1f;                // ReplayGain 线性增益（新输入生效）

        TrackInput _preloaded;               // 预热解码器（未入混音器）
        string _preloadedPath;

        /// <summary>当前播放会话 ID（每次 Load/CrossfadeTo 递增；切歌竞态过滤依据）。</summary>
        public long SessionId { get { lock (_lock) { return _sessionId; } } }

        public event EventHandler<PlaybackStateChangedEventArgs> StateChanged;
        public event EventHandler<SpectrumDataEventArgs> SpectrumDataReady;
        public event EventHandler<PlaybackEndedEventArgs> PlaybackEnded;

        public PlaybackState State { get; private set; }

        public TimeSpan Position
        {
            get { lock (_lock) { return _current != null && !_current.ReaderDisposed ? _current.Reader.CurrentTime : TimeSpan.Zero; } }
            set
            {
                lock (_lock)
                {
                    if (_current == null || _current.ReaderDisposed) return;
                    // 越界钳制到 [0, Duration]（负值会让 MF 抛 ArgumentOutOfRangeException）
                    if (value < TimeSpan.Zero) value = TimeSpan.Zero;
                    if (_current.Reader.TotalTime > TimeSpan.Zero && value > _current.Reader.TotalTime)
                        value = _current.Reader.TotalTime;
                    _current.Reader.CurrentTime = value;
                }
            }
        }

        public TimeSpan Duration
        {
            get { lock (_lock) { return _current != null && !_current.ReaderDisposed ? _current.Reader.TotalTime : TimeSpan.Zero; } }
        }

        public float Volume
        {
            get { return _output != null ? _output.Volume : _desiredVolume; }
            set
            {
                _desiredVolume = Math.Max(0f, Math.Min(1f, value));
                if (_output != null) _output.Volume = _desiredVolume;
            }
        }

        public string CurrentPath { get { lock (_lock) { return _currentPath; } } }

        public PlayerEngine()
        {
            State = PlaybackState.Stopped;
            _mixer = new MixingSampleProvider(AudioDecoders.MixerFormat) { ReadFully = false };
            _mixer.MixerInputEnded += OnMixerInputEnded;
            _capture = new SampleCaptureProvider(_mixer);
            _output = CreateOutput(_capture);
            if (_output != null) _output.Volume = _desiredVolume;
        }

        /// <summary>创建常驻输出设备；WASAPI 独占开启时优先尝试，失败回退 WaveOutEvent。</summary>
        IWavePlayer CreateOutput(ISampleProvider source)
        {
            if (Settings.Get("wasapi_exclusive", "0") == "1")
            {
                try
                {
                    var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    var wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, false, 150);
                    wasapi.Init(source);
                    MainViewModel.Dbg("Output: WASAPI Exclusive");
                    return wasapi;
                }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("WASAPI Exclusive init FAIL -> fallback WaveOutEvent: " + ex.Message);
                }
            }
            var waveOut = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 4 };
            waveOut.PlaybackStopped += OnDeviceStopped;
            waveOut.Init(source);
            return waveOut;
        }

        /* ============================================================
         * 加载 / 播放控制
         * ============================================================ */

        /// <summary>加载音频文件（手动切歌：立即硬切，移除旧输入）。</summary>
        public bool Load(string path)
        {
            lock (_lock)
            {
                if (_disposed) return false;
                _sessionId++;
                long session = _sessionId;
                RemoveAllInputs();

                if (!File.Exists(path)) return false;
                TrackInput input = BuildInput(path, session);
                if (input == null) return false;

                _current = input;
                _currentPath = path;
                State = PlaybackState.Stopped;
                RaiseStateChanged();
                MainViewModel.Dbg("Load ok (mixer): " + path);
                return true;
            }
        }

        /// <summary>跨淡切换：新输入淡入混入，当前输入淡出后移除（Crossfade 用）。</summary>
        public bool CrossfadeTo(string path, double fadeSeconds)
        {
            lock (_lock)
            {
                if (_disposed) return false;
                _sessionId++;
                long session = _sessionId;

                if (!File.Exists(path)) return false;
                TrackInput input = BuildInput(path, session);
                if (input == null) return false;

                input.Gain.BeginFadeIn(fadeSeconds);
                AddInput(input);

                TrackInput old = _current;
                _current = input;
                _currentPath = path;
                if (old != null)
                    old.Gain.BeginFadeOut(0, fadeSeconds);
                ScheduleOldInputRemoval(old, fadeSeconds);

                if (State != PlaybackState.Playing)
                {
                    State = PlaybackState.Playing;
                    try { if (_output != null) _output.Play(); } catch { }
                }
                RaiseStateChanged();
                return true;
            }
        }

        /// <summary>把输入接入混音器（统一入口，维护 InMixer 标记）。</summary>
        void AddInput(TrackInput input)
        {
            _mixer.AddMixerInput(input.Gain);
            input.InMixer = true;
            _inputs.Add(input);
        }

        public void Play()
        {
            lock (_lock)
            {
                if (_disposed || _output == null) return;
                if (State != PlaybackState.Playing)
                {
                    try { _output.Play(); } catch { }
                    State = PlaybackState.Playing;
                    RaiseStateChanged();
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (_disposed || _output == null) return;
                if (State == PlaybackState.Playing)
                {
                    try { _output.Pause(); } catch { }
                    State = PlaybackState.Paused;
                    RaiseStateChanged();
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _deviceStopping = true;   // 主动停止：忽略设备 PlaybackStopped
                RemoveAllInputs();
                try { if (_output != null) _output.Stop(); } catch { }
                State = PlaybackState.Stopped;
                RaiseStateChanged();
            }
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            RaiseStateChanged();
        }

        /// <summary>设置 ReplayGain 线性增益（1=不变），对当前输入实时生效。</summary>
        public void SetReplayGain(float linear)
        {
            lock (_lock)
            {
                _rgLinear = Math.Max(0f, Math.Min(8f, linear));
                if (_current != null) _current.Gain.BaseGain = _rgLinear;
            }
        }

        /// <summary>
        /// 预加载解码器（后台线程打开，下一首 Load/CrossfadeTo 时零成本取用）。
        /// 幂等：同一路径重复调用只打开一次。
        /// </summary>
        public void Preload(string path)
        {
            lock (_lock)
            {
                if (_disposed || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (_preloadedPath == path) return;
                DisposePreloaded();
                _preloadedPath = path;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                WaveStream reader = null;
                try { reader = AudioDecoders.Open(path); }
                catch { reader = null; }
                lock (_lock)
                {
                    if (_disposed) { try { if (reader != null) reader.Dispose(); } catch { } return; }
                    if (_preloadedPath != path) { try { if (reader != null) reader.Dispose(); } catch { } return; }
                    if (reader != null) _preloaded = new TrackInput { Path = path, Reader = reader };
                }
            });
        }

        /// <summary>取预载解码器（路径匹配才用，否则现开；不匹配的旧预载随手丢弃）。</summary>
        WaveStream TakePreloaded(string path)
        {
            if (_preloaded != null && string.Equals(_preloadedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var reader = _preloaded.Reader;
                _preloaded = null;
                _preloadedPath = null;
                return reader;
            }
            DisposePreloaded();
            return null;
        }

        /// <summary>构建一条曲目输入：优先取预载解码器，否则现开；归一化 + 增益包络。</summary>
        TrackInput BuildInput(string path, long session)
        {
            try
            {
                WaveStream reader = TakePreloaded(path) ?? AudioDecoders.Open(path);
                ISampleProvider normalized = AudioDecoders.NormalizeToMixer(reader.ToSampleProvider());
                var gain = new GainFadeSampleProvider(normalized) { BaseGain = _rgLinear };
                return new TrackInput { SessionId = session, Path = path, Reader = reader, Gain = gain };
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("BuildInput FAIL: " + path + " -> " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        void DisposePreloaded()
        {
            if (_preloaded != null)
            {
                try { _preloaded.Reader.Dispose(); } catch { }
                _preloaded = null;
            }
            _preloadedPath = null;
        }

        /// <summary>淡出完成后兜底移除旧输入（正常情况下其自然结束会先触发清理）。</summary>
        void ScheduleOldInputRemoval(TrackInput old, double fadeSeconds)
        {
            if (old == null) return;
            var timer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_current != old && _inputs.Contains(old))
                        RemoveInput(old);
                }
            }, null, (int)(fadeSeconds * 1000) + 800, Timeout.Infinite);
        }

        void RemoveInput(TrackInput input)
        {
            if (_inputs.Remove(input))
            {
                if (input.InMixer)
                {
                    try { _mixer.RemoveMixerInput(input.Gain); } catch { }
                    input.InMixer = false;
                }
                input.ReaderDisposed = true;
                try { input.Reader.Dispose(); } catch { }
            }
        }

        void RemoveAllInputs()
        {
            foreach (TrackInput input in _inputs)
            {
                if (input.InMixer)
                {
                    try { _mixer.RemoveMixerInput(input.Gain); } catch { }
                    input.InMixer = false;
                }
                input.ReaderDisposed = true;
                try { input.Reader.Dispose(); } catch { }
            }
            _inputs.Clear();
            _current = null;
        }

        /* ============================================================
         * 结束判定 / 设备事件
         * ============================================================ */

        /// <summary>曲目输入自然播完（混音器移除该输入时触发）→ 携带会话 ID 上报结束。</summary>
        void OnMixerInputEnded(object sender, SampleProviderEventArgs e)
        {
            TrackInput input = null;
            lock (_lock)
            {
                if (_disposed) return;
                foreach (TrackInput t in _inputs)
                {
                    if (ReferenceEquals(t.Gain, e.SampleProvider)) { input = t; break; }
                }
                if (input != null)
                {
                    _inputs.Remove(input);
                    input.ReaderDisposed = true;
                    try { input.Reader.Dispose(); } catch { }
                }
                if (input != null && ReferenceEquals(input, _current))
                {
                    // 当前曲目自然播完：会话仍有效（未发生新的 Load/CrossfadeTo）
                    _current = null;
                    State = PlaybackState.Stopped;
                    var handler = PlaybackEnded;
                    if (handler != null)
                        handler(this, new PlaybackEndedEventArgs { SessionId = input.SessionId, Path = input.Path });
                    RaiseStateChanged();
                }
            }
        }

        /// <summary>设备级 PlaybackStopped：只在主动 Stop()/Dispose() 时发生，直接忽略。</summary>
        void OnDeviceStopped(object sender, StoppedEventArgs e)
        {
            _deviceStopping = false;
            // 自然结束走 OnMixerInputEnded；这里无需处理。
            // （设备异常中断的场景由"播放无声"的用户感知 + 重启应用兜底，概率极低）
        }

        /// <summary>主动拉取当前频谱数据（由 UI 定时器调用，约 30fps）。</summary>
        public void PullSpectrum()
        {
            if (_capture == null || _output == null || State != PlaybackState.Playing) return;

            float[] samples = _capture.GetLastSamples();
            if (samples == null || samples.Length == 0) return;

            var handler = SpectrumDataReady;
            if (handler != null)
            {
                handler(this, new SpectrumDataEventArgs
                {
                    Samples = samples,
                    Channels = _capture.WaveFormat.Channels,
                    SampleRate = _capture.WaveFormat.SampleRate
                });
            }
        }

        void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null)
            {
                handler(this, new PlaybackStateChangedEventArgs
                {
                    State = State,
                    Position = Position,
                    Duration = Duration
                });
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _deviceStopping = true;
                RemoveAllInputs();
                DisposePreloaded();
                try { if (_output != null) _output.Dispose(); } catch { }
                _output = null;
            }
        }
    }
}
