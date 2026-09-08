/* ============================================================
 * PlayerEngine.cs — 音频播放引擎（NAudio 实现，P2 频谱真实化）
 * 从 MainWindow 提取，独立负责音频输出生命周期、播放控制、
 * 进度通知、样本抓取（用于真实频谱，非系统环回）。
 * ============================================================ */
using System;
using System.IO;
using NAudio.Wave;

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

    /// <summary>
    /// 样本抓取包装器：在播放链中插入，复制 PCM 数据给频谱模块。
    /// 实现 ISampleProvider，不干扰播放线程。
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
    /// 音频播放引擎。使用 NAudio 输出，支持样本抓取实现真实频谱。
    /// </summary>
    public class PlayerEngine : IDisposable
    {
        private WaveOutEvent _waveOut;
        private WaveStream _audioFile;
        private SampleCaptureProvider _capture;
        private string _currentPath;
        private bool _disposed;
        private bool _isPlaying;
        private float _desiredVolume = 1.0f;   // WaveOut 重建后重放（修复重启后音量丢失）
        private long _playStartTicks;          // 本次 Play() 的 UTC 计时（快速停止防护用）
        private int _fastStopRetries;          // 快速停止重试计数（防死循环）

        public event EventHandler<PlaybackStateChangedEventArgs> StateChanged;
        public event EventHandler<SpectrumDataEventArgs> SpectrumDataReady;
        public event EventHandler PlaybackEnded;

        public PlaybackState State { get; private set; }

        public TimeSpan Position
        {
            get { return _audioFile != null ? _audioFile.CurrentTime : TimeSpan.Zero; }
            set
            {
                if (_audioFile == null) return;
                // 快退键（←）在歌曲开头 5 秒内会算出负位置，MediaFoundationReader 对
                // 负值直接抛 ArgumentOutOfRangeException（有全局钩子兜底不闪退，
                // 但 seek 状态已乱）；越界统一钳制到 [0, Duration]
                if (value < TimeSpan.Zero) value = TimeSpan.Zero;
                if (_audioFile.TotalTime > TimeSpan.Zero && value > _audioFile.TotalTime)
                    value = _audioFile.TotalTime;
                _audioFile.CurrentTime = value;
            }
        }

        public TimeSpan Duration
        {
            get { return _audioFile != null ? _audioFile.TotalTime : TimeSpan.Zero; }
        }

        public float Volume
        {
            get { return _waveOut != null ? _waveOut.Volume : _desiredVolume; }
            set
            {
                _desiredVolume = Math.Max(0f, Math.Min(1f, value));
                if (_waveOut != null) _waveOut.Volume = _desiredVolume;
            }
        }

        public string CurrentPath { get { return _currentPath; } }

        public PlayerEngine()
        {
            State = PlaybackState.Stopped;
        }

        /// <summary>加载音频文件。</summary>
        public bool Load(string path)
        {
            try
            {
                Stop();
                DisposeWaveOut();

                if (!File.Exists(path)) return false;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool isVorbis = ext == ".ogg" || ext == ".oga";
                bool isOpus = ext == ".opus";

                // OGG 使用 NVorbis 流式读取（VorbisWaveReader）。
                // OPUS 使用 Concentus 纯托管解码（OpusWaveReader）：
                // Media Foundation 不支持裸 .opus 字节流（0xC00D36C4），
                // NVorbis 仅支持 Vorbis 编码，二者均不可用，必须自解码。
                if (isVorbis)
                    _audioFile = new VorbisWaveReader(path);
                else if (isOpus)
                    _audioFile = new OpusWaveReader(path);
                else
                    _audioFile = new AudioFileReader(path);

                _currentPath = path;

                // 样本抓取包装器：用于真实频谱（非系统环回）
                // AudioFileReader 和 VorbisWaveReader 均实现 ISampleProvider
                _capture = new SampleCaptureProvider(_audioFile as ISampleProvider);

                _waveOut = new WaveOutEvent();
                _waveOut.DesiredLatency = 100;
                _waveOut.NumberOfBuffers = 3;
                _waveOut.Volume = _desiredVolume;   // 恢复用户音量（新 WaveOut 默认 100%）
                _waveOut.PlaybackStopped += OnPlaybackStopped;
                _waveOut.Init(_capture);

                State = PlaybackState.Stopped;
                _isPlaying = false;
                RaiseStateChanged();
                Aurora.MainViewModel.Dbg("Load ok: " + path);
                return true;
            }
            catch (Exception ex)
            {
                Aurora.MainViewModel.Dbg("Load FAIL: " + path + " -> " + ex.GetType().Name + ": " + ex.Message);
                DisposeWaveOut();
                return false;
            }
        }

        public void Play()
        {
            if (_disposed || _waveOut == null) return;
            if (State != PlaybackState.Playing)
            {
                _waveOut.Play();
                State = PlaybackState.Playing;
                _isPlaying = true;
                _playStartTicks = DateTime.UtcNow.Ticks;
                _fastStopRetries = 0;
                RaiseStateChanged();
            }
        }

        public void Pause()
        {
            if (_disposed || _waveOut == null) return;
            if (State == PlaybackState.Playing)
            {
                _waveOut.Pause();
                State = PlaybackState.Paused;
                _isPlaying = false;
                RaiseStateChanged();
            }
        }

        public void Stop()
        {
            if (_disposed) return;
            if (_waveOut != null)
            {
                _isPlaying = false;
                _waveOut.Stop();
            }
            if (_audioFile != null)
            {
                _audioFile.Position = 0;
            }
            State = PlaybackState.Stopped;
            RaiseStateChanged();
        }

        public void Seek(TimeSpan position)
        {
            if (_disposed || _audioFile == null) return;
            _audioFile.CurrentTime = position;
            RaiseStateChanged();
        }

        /// <summary>主动拉取当前频谱数据（由 UI 定时器调用，约 30fps）。</summary>
        public void PullSpectrum()
        {
            if (_capture == null || _audioFile == null || State != PlaybackState.Playing) return;

            float[] samples = _capture.GetLastSamples();
            if (samples == null || samples.Length == 0) return;

            var handler = SpectrumDataReady;
            if (handler != null)
            {
                handler(this, new SpectrumDataEventArgs
                {
                    Samples = samples,
                    Channels = _audioFile.WaveFormat.Channels,
                    SampleRate = _audioFile.WaveFormat.SampleRate
                });
            }
        }

        private void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // 自然结束：_isPlaying 仍为 true（主动 Stop()/Pause() 会先置 false 再动作，
            // 因此走到这里的 stopped 一定是播到末尾或设备中断，均按结束处理。
            // 不能用 CurrentTime >= TotalTime 判定——MF 解码的 MP3 常停在比
            // TotalTime 略小的位置（帧填充样本），精确比较会漏判导致不切歌。
            Aurora.MainViewModel.Dbg("OnPlaybackStopped: path=" + _currentPath + " isPlaying=" + _isPlaying);
            if (_isPlaying)
            {
                // 快速停止防护：点击切歌时旧 WaveOut 正在活跃播放，Stop()+Dispose() 后
                // 立即新建 WaveOut 播放（尤其 MediaFoundation 解码的 MP3），设备清理与新
                // 实例竞争会导致播放线程瞬间异常退出，触发 PlaybackStopped——表现与
                // "自然播完"完全相同（实测 Play 后 7ms 即 stopped）。若不防护，会被误判
                // 为切歌条件，用户点击的歌被自动连播覆盖。
                // 判据：Play() 后 300ms 内就 stopped 且位置仍在起点 → 重试播放而非切歌；
                // 连续 3 次仍失败则放行（按结束处理，避免死循环）。
                var sincePlay = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _playStartTicks);
                if (sincePlay < TimeSpan.FromMilliseconds(300) &&
                    Position < TimeSpan.FromMilliseconds(200) &&
                    _fastStopRetries < 3)
                {
                    _fastStopRetries++;
                    Aurora.MainViewModel.Dbg("PlaybackStopped too soon (" + sincePlay.TotalMilliseconds + "ms) -> retry #" + _fastStopRetries);
                    try
                    {
                        _waveOut.Play();
                        _playStartTicks = DateTime.UtcNow.Ticks;
                        return;   // 保持 State=Playing / _isPlaying=true
                    }
                    catch (Exception ex)
                    {
                        Aurora.MainViewModel.Dbg("retry Play FAIL: " + ex.Message);
                    }
                }

                _isPlaying = false;
                State = PlaybackState.Stopped;
                var handler = PlaybackEnded;
                if (handler != null) handler(this, EventArgs.Empty);
                RaiseStateChanged();
            }
        }

        private void RaiseStateChanged()
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

        private void DisposeWaveOut()
        {
            if (_waveOut != null)
            {
                _waveOut.PlaybackStopped -= OnPlaybackStopped;
                _waveOut.Dispose();
                _waveOut = null;
            }
            _capture = null;
            if (_audioFile != null)
            {
                _audioFile.Dispose();
                _audioFile = null;
            }
            _currentPath = null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                DisposeWaveOut();
                _disposed = true;
            }
        }
    }
}
