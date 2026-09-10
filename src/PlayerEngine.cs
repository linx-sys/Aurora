/* ============================================================
 * PlayerEngine.cs — 音频播放引擎（协调层，阶段 4 拆分后）
 *
 * 架构：常驻输出设备
 *   WaveOutEvent（或 WASAPI Exclusive，IAudioOutput 抽象）
 *     ← SampleCaptureProvider（频谱采样，SpectrumCapture.cs）
 *     ← MixingSampleProvider（48kHz 立体声 float，ReadFully=false）
 *        ← 每曲目输入：解码器 → 格式归一化 → GainFadeSampleProvider
 *          （输入集合/预载/淡出移除管理在 TrackInputManager.cs）
 *
 * 本类职责：状态机与会话 ID、输出设备生命周期、ReplayGain 应用、
 *           自然结束上报、诊断信息。不含输入集合管理与预载细节。
 * ============================================================ */
using System;
using System.IO;
using System.Text;
using NAudio.Wave;
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
    /// 音频播放引擎（IPlaybackService 唯一实现，协调层）。
    /// 频谱采样在 SpectrumCapture.cs、曲目输入/预载管理在 TrackInputManager.cs。
    /// </summary>
    public class PlayerEngine : IPlaybackService
    {
        IAudioOutput _output;
        readonly Func<ISampleProvider, IAudioOutput> _outputFactory;   // P1-4：输出设备工厂（测试注入 fake）
        readonly Func<string, WaveStream> _decoder;                    // P1-4：解码入口（测试注入 fake）
        SampleCaptureProvider _capture;      // 频谱采样（挂在混音器输出）
        MixingSampleProvider _mixer;
        TrackInputManager _inputs;           // 曲目输入/预载管理（阶段 4 拆分）
        readonly object _lock = new object();

        long _sessionId;
        string _currentPath;
        bool _disposed;
        bool _deviceStopping;                // 主动 Stop 时忽略设备 PlaybackStopped
        bool _outputFailed;                  // 设备初始化失败（不再重试，播放静默降级）
        string _outputDescription = "未初始化";   // 诊断用（阶段 7）
        float _desiredVolume = 1.0f;         // 主音量（WaveOut.Volume，持久保持）
        float _rgLinear = 1f;                // ReplayGain 线性增益（新输入生效）

        /// <summary>当前播放会话 ID（每次 Load/CrossfadeTo 递增；切歌竞态过滤依据）。</summary>
        public long SessionId { get { lock (_lock) { return _sessionId; } } }

        public event EventHandler<PlaybackStateChangedEventArgs> StateChanged;
        public event EventHandler<SpectrumDataEventArgs> SpectrumDataReady;
        public event EventHandler<PlaybackEndedEventArgs> PlaybackEnded;

        public PlaybackState State { get; private set; }

        TrackInput CurrentInput { get { return _inputs.Current; } }

        public TimeSpan Position
        {
            get
            {
                lock (_lock)
                {
                    TrackInput cur = CurrentInput;
                    return cur != null && !cur.ReaderDisposed ? cur.Reader.CurrentTime : TimeSpan.Zero;
                }
            }
            set
            {
                lock (_lock)
                {
                    TrackInput cur = CurrentInput;
                    if (cur == null || cur.ReaderDisposed) return;
                    // 越界钳制到 [0, Duration]（负值会让 MF 抛 ArgumentOutOfRangeException）
                    if (value < TimeSpan.Zero) value = TimeSpan.Zero;
                    if (cur.Reader.TotalTime > TimeSpan.Zero && value > cur.Reader.TotalTime)
                        value = cur.Reader.TotalTime;
                    cur.Reader.CurrentTime = value;
                }
            }
        }

        public TimeSpan Duration
        {
            get
            {
                lock (_lock)
                {
                    TrackInput cur = CurrentInput;
                    return cur != null && !cur.ReaderDisposed ? cur.Reader.TotalTime : TimeSpan.Zero;
                }
            }
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

        /// <summary>生产构造：真实输出设备（WaveOut / WASAPI 独占回退）+ 真实解码器。</summary>
        public PlayerEngine() : this(null, null) { }

        /// <summary>
        /// 注入构造（P1-4 可测试性）：输出工厂/解码器为 null 时用生产默认值。
        /// 生产路径（factory=null）不在此同步建设备——设备创建后台预热（P2-5a 启动提速），
        /// 首次 Play/Pause/Stop 时按需补建；测试注入则立即创建（同步语义可预期）。
        /// </summary>
        public PlayerEngine(Func<ISampleProvider, IAudioOutput> outputFactory, Func<string, WaveStream> decoder)
        {
            State = PlaybackState.Stopped;
            _outputFactory = outputFactory ?? DefaultCreateOutput;
            _decoder = decoder ?? AudioDecoders.Open;
            _mixer = new MixingSampleProvider(AudioDecoders.MixerFormat) { ReadFully = false };
            _mixer.MixerInputEnded += OnMixerInputEnded;
            _inputs = new TrackInputManager(_mixer, _decoder, _lock);
            _capture = new SampleCaptureProvider(_mixer);
            if (outputFactory != null)
            {
                EnsureOutput();
            }
            else
            {
                // 后台预热设备：不抢 UI 线程启动窗口；失败置 _outputFailed，播放时不再重试
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_lock) { if (!_disposed) EnsureOutput(); }
                });
            }
        }

        /// <summary>确保输出设备就绪（调用方需持 _lock）；失败置 _outputFailed 不再重试。</summary>
        void EnsureOutput()
        {
            if (_output != null || _outputFailed) return;
            try
            {
                _output = _outputFactory(_capture);
                if (_output != null)
                {
                    _output.Volume = _desiredVolume;
                    _output.PlaybackStopped += OnDeviceStopped;
                    _outputDescription = DescribeOutput(_output);
                }
                else
                {
                    _outputFailed = true;
                    _outputDescription = "初始化失败（返回空）";
                }
            }
            catch (Exception ex)
            {
                _outputFailed = true;
                _outputDescription = "初始化失败";
                MainViewModel.Dbg("Output init FAIL: " + ex.Message);
            }
        }

        static string DescribeOutput(IAudioOutput output)
        {
            if (output is WasapiAudioOutput) return "WASAPI 独占";
            if (output is WaveOutAudioOutput) return "WaveOutEvent（共享模式）";
            return output.GetType().Name;
        }

        /// <summary>默认输出工厂：WASAPI 独占开启时优先尝试，失败回退 WaveOutEvent。</summary>
        static IAudioOutput DefaultCreateOutput(ISampleProvider source)
        {
            if (Settings.Get("wasapi_exclusive", "0") == "1")
            {
                try
                {
                    var wasapi = new WasapiAudioOutput(source);
                    MainViewModel.Dbg("Output: WASAPI Exclusive");
                    return wasapi;
                }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("WASAPI Exclusive init FAIL -> fallback WaveOutEvent: " + ex.Message);
                }
            }
            return new WaveOutAudioOutput(source);
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
                _inputs.RemoveAll();

                if (!File.Exists(path)) return false;
                TrackInput input = _inputs.BuildInput(path, session, _rgLinear);
                if (input == null) return false;
                _inputs.Add(input);   // 关键：接入混音器（缺失会导致 mixer 无输入→永久静音、进度不动）

                _inputs.Current = input;
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
                TrackInput input = _inputs.BuildInput(path, session, _rgLinear);
                if (input == null) return false;

                input.Gain.BeginFadeIn(fadeSeconds);
                _inputs.Add(input);

                TrackInput old = _inputs.Current;
                _inputs.Current = input;
                _currentPath = path;
                if (old != null)
                    old.Gain.BeginFadeOut(0, fadeSeconds);
                _inputs.ScheduleOldInputRemoval(old, fadeSeconds);

                if (State != PlaybackState.Playing)
                {
                    State = PlaybackState.Playing;
                    try { if (_output != null) _output.Play(); } catch { }
                }
                RaiseStateChanged();
                return true;
            }
        }

        public void Play()
        {
            lock (_lock)
            {
                if (_disposed) return;
                EnsureOutput();
                if (_output == null) return;
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
                _inputs.RemoveAll();
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
                TrackInput cur = CurrentInput;
                if (cur != null) cur.Gain.BaseGain = _rgLinear;
            }
        }

        /// <summary>预加载解码器（转发 TrackInputManager，后台线程打开）。</summary>
        public void Preload(string path)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _inputs.Preload(path);
            }
        }

        /* ============================================================
         * 结束判定 / 设备事件
         * ============================================================ */

        /// <summary>曲目输入自然播完（混音器移除该输入时触发）→ 携带会话 ID 上报结束。</summary>
        void OnMixerInputEnded(object sender, SampleProviderEventArgs e)
        {
            lock (_lock)
            {
                if (_disposed) return;
                TrackInput input = _inputs.TakeEnded(e.SampleProvider);
                if (input != null && ReferenceEquals(input, _inputs.Current))
                {
                    // 当前曲目自然播完：会话仍有效（未发生新的 Load/CrossfadeTo）
                    _inputs.Current = null;
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
                _inputs.RemoveAll();
                _inputs.MarkDisposed();
                try { if (_output != null) _output.Dispose(); } catch { }
                _output = null;
            }
        }

        /* ============================================================
         * 音频诊断（阶段 7）
         * ============================================================ */

        /// <summary>组装当前音频链路诊断文本（UI 线程调用；只读，不改状态）。</summary>
        public string GetDiagnostics()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                TrackInput cur = CurrentInput;
                if (cur == null || cur.ReaderDisposed)
                {
                    sb.AppendLine("当前曲目：无");
                }
                else
                {
                    var wf = cur.Reader.WaveFormat;
                    string ext = string.IsNullOrEmpty(_currentPath) ? "-" :
                        Path.GetExtension(_currentPath).TrimStart('.').ToUpperInvariant();
                    sb.AppendLine("当前曲目：" + (_currentPath ?? "-"));
                    sb.AppendLine("解码格式：" + ext + " / " + wf.SampleRate + " Hz / " +
                        wf.BitsPerSample + " bit / " +
                        (wf.Channels == 1 ? "单声道" : wf.Channels + " 声道"));
                    sb.AppendLine("总时长：" + cur.Reader.TotalTime.ToString(@"mm\:ss"));
                    sb.AppendLine("混音器输入数：" + _inputs.InputCount);
                }
                double rgDb = 20.0 * Math.Log10(Math.Max(_rgLinear, 0.0001));
                sb.AppendLine("ReplayGain：" + (PlaybackCoordinator.ReplayGainEnabled
                    ? rgDb.ToString("+0.0;-0.0;0.0") + " dB（线性 " + _rgLinear.ToString("0.000") + "）"
                    : "已关闭"));
                double cf = PlaybackCoordinator.CrossfadeSeconds;
                sb.AppendLine("跨淡入淡出：" + (cf > 0 ? cf.ToString("0.#") + " 秒" : "关闭"));
                sb.AppendLine("输出设备：" + _outputDescription +
                    (Settings.Get("wasapi_exclusive", "0") == "1" ? "（已请求独占）" : ""));
                sb.AppendLine("输出就绪：" + (_outputFailed ? "失败（播放将无声）" : (_output != null ? "是" : "后台预热中")));
                sb.AppendLine("会话 ID：" + _sessionId);
                sb.AppendLine("引擎版本：Aurora " + AppInfo.Version);
                return sb.ToString();
            }
        }
    }
}
