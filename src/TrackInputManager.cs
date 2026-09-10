/* ============================================================
 * TrackInputManager.cs — 曲目输入与预载管理（阶段 4 自 PlayerEngine 拆出）
 * 职责：混音器输入集合（添加/移除/清空）、当前曲目输入、
 *       解码构建（优先取预载）、下一首预热、淡出后定时移除。
 * 线程约定：所有方法须持有引擎的 _lock（共享锁对象由引擎传入，
 * 本类自身不再加锁）；Preload 内部的后台线程自行取锁。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Aurora
{
    /// <summary>混音器中的一条曲目输入。</summary>
    internal class TrackInput
    {
        public long SessionId;
        public string Path = null!;
        public WaveStream Reader = null!;            // 源格式（Position/TotalTime）
        public GainFadeSampleProvider Gain = null!;  // 归一化后的混音器输入
        public bool InMixer;
        public bool ReaderDisposed;                  // Reader 已释放（防 ObjectDisposedException）
    }

    internal class TrackInputManager
    {
        readonly MixingSampleProvider _mixer;
        readonly Func<string, WaveStream> _decoder;
        readonly object _lock;               // 与引擎共享的锁（重入安全）
        readonly List<TrackInput> _inputs = new List<TrackInput>();

        TrackInput? _current;
        TrackInput? _preloaded;              // 预热解码器（未入混音器）
        string? _preloadedPath;
        bool _disposed;

        public TrackInputManager(MixingSampleProvider mixer, Func<string, WaveStream> decoder, object sharedLock)
        {
            _mixer = mixer;
            _decoder = decoder;
            _lock = sharedLock;
        }

        /// <summary>当前曲目输入（引擎的 Position/Duration/Current 语义来源）。</summary>
        public TrackInput? Current
        {
            get { return _current; }
            set { _current = value; }
        }

        /// <summary>当前活跃输入数（诊断用；须持引擎锁读取）。</summary>
        public int InputCount { get { return _inputs.Count; } }

        /// <summary>引擎 Dispose 时调用：停止预载后台行为。</summary>
        public void MarkDisposed() { _disposed = true; }

        /* ---------- 输入集合 ---------- */

        public void Add(TrackInput input)
        {
            _mixer.AddMixerInput(input.Gain);
            input.InMixer = true;
            _inputs.Add(input);
        }

        public void Remove(TrackInput input)
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

        public void RemoveAll()
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

        /// <summary>
        /// 混音器输入自然结束回调：按 Gain 引用找到对应输入并移除（含释放）。
        /// 返回被移除的输入；不属于任何活跃输入时返回 null。
        /// </summary>
        public TrackInput? TakeEnded(ISampleProvider gain)
        {
            TrackInput? input = null;
            foreach (TrackInput t in _inputs)
            {
                if (ReferenceEquals(t.Gain, gain)) { input = t; break; }
            }
            if (input != null) Remove(input);
            return input;
        }

        /* ---------- 构建输入 ---------- */

        /// <summary>构建一条曲目输入：优先取预载解码器，否则现开；归一化 + 增益包络。</summary>
        public TrackInput? BuildInput(string path, long session, float rgLinear)
        {
            try
            {
                WaveStream reader = TakePreloaded(path) ?? _decoder(path);
                ISampleProvider normalized = AudioDecoders.NormalizeToMixer(reader.ToSampleProvider());
                var gain = new GainFadeSampleProvider(normalized) { BaseGain = rgLinear };
                return new TrackInput { SessionId = session, Path = path, Reader = reader, Gain = gain };
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("BuildInput FAIL: " + path + " -> " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>淡出完成后兜底移除旧输入（正常情况下其自然结束会先触发清理）。</summary>
        public void ScheduleOldInputRemoval(TrackInput? old, double fadeSeconds)
        {
            if (old == null) return;
            var timer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_current != old && _inputs.Contains(old))
                        Remove(old);
                }
            }, null, (int)(fadeSeconds * 1000) + 800, Timeout.Infinite);
        }

        /* ---------- 预加载 ---------- */

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
                WaveStream? reader = null;
                try { reader = _decoder(path); }
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
        WaveStream? TakePreloaded(string path)
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

        void DisposePreloaded()
        {
            if (_preloaded != null)
            {
                try { _preloaded.Reader.Dispose(); } catch { }
                _preloaded = null;
            }
            _preloadedPath = null;
        }
    }
}
