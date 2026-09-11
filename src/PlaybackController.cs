/* ============================================================
 * PlaybackController.cs — 播放模式控制器（状态机）
 * 从 MainViewModel 抽离，管理循环/单曲/随机等播放模式的状态转换。
 * 所有方法均为纯逻辑，不直接操作 UI，便于单元测试。
 * 随机模式维护播放轨迹：上一首=真正回退刚听过的歌，下一首可"前进"重播。
 * ============================================================ */

using System;
using System.Collections.Generic;

namespace Aurora
{
    /// <summary>
    /// 播放模式控制器。封装播放列表的下一首/上一首选择逻辑，
    /// 支持列表循环、单曲循环、随机播放三种模式。
    /// </summary>
    public class PlaybackController
    {
        private readonly Random _rng = new Random();
        private readonly List<Track> _shuffleHistory = new List<Track>();   // 随机模式播放轨迹
        private int _shufflePos = -1;                                       // 轨迹当前位置

        PlayMode _mode = PlayMode.ListRepeat;
        Track? _current;
        public event EventHandler? ModeChanged;

        /// <summary>当前播放模式；非法持久化值回退到列表循环。</summary>
        public PlayMode Mode
        {
            get { return _mode; }
            set
            {
                var valid = Enum.IsDefined(typeof(PlayMode), value) ? value : PlayMode.ListRepeat;
                if (_mode == valid) return;
                _mode = valid;
                ResetShuffleHistory();
                if (_current != null) RecordPlay(_current);
                ModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 获取下一首曲目（播放结束自动触发时使用）。
        /// 单曲循环模式返回 null（调用方应重新播放当前曲目）。
        /// </summary>
        public Track? GetNextForAuto(IList<Track> tracks, Track? current)
        {
            if (tracks == null || tracks.Count == 0) return null;
            if (Mode == PlayMode.SingleRepeat) return null; // 单曲循环由调用方处理
            return GetNextInternal(tracks, current);
        }

        /// <summary>
        /// 获取下一首曲目（用户手动点击下一首时使用）。
        /// 单曲循环模式也会切到下一首。
        /// </summary>
        public Track? GetNextForManual(IList<Track> tracks, Track? current)
        {
            if (tracks == null || tracks.Count == 0) return null;
            return GetNextInternal(tracks, current);
        }

        /// <summary>获取上一首曲目（非随机模式：列表序号回退）。</summary>
        public Track? GetPrev(IList<Track> tracks, Track? current)
        {
            if (tracks == null || tracks.Count == 0) return null;
            int idx = current != null ? tracks.IndexOf(current) : 0;
            idx = (idx - 1 + tracks.Count) % tracks.Count;
            return tracks[idx];
        }

        /// <summary>随机模式：回退播放轨迹。无可回退返回 false（调用方走列表回退）。</summary>
        public bool TryGetShufflePrev(out Track? prev)
        {
            prev = null;
            if (Mode != PlayMode.Shuffle || _shufflePos <= 0) return false;
            prev = _shuffleHistory[--_shufflePos];
            _current = prev;
            return true;
        }

        /// <summary>随机模式：沿轨迹前进（"下一首"优先重播回退过的歌）。无可前进返回 false。</summary>
        public bool TryGetShuffleNext(out Track? next)
        {
            next = null;
            if (Mode != PlayMode.Shuffle || _shufflePos >= _shuffleHistory.Count - 1) return false;
            next = _shuffleHistory[++_shufflePos];
            _current = next;
            return true;
        }

        /// <summary>
        /// 随机模式：记录一次实际播放。连续重复不记录；
        /// 回退后播放新曲会截断"前进"分支（与浏览器历史一致）。上限 200 首。
        /// </summary>
        public void RecordPlay(Track t)
        {
            if (t == null) return;
            _current = t;
            if (Mode != PlayMode.Shuffle) return;
            int last = _shuffleHistory.Count - 1;
            if (_shufflePos >= 0 && ReferenceEquals(_shuffleHistory[_shufflePos], t)) return;
            if (_shufflePos < last) _shuffleHistory.RemoveRange(_shufflePos + 1, last - _shufflePos);
            _shuffleHistory.Add(t);
            if (_shuffleHistory.Count > 200)
                _shuffleHistory.RemoveRange(0, _shuffleHistory.Count - 200);
            _shufflePos = _shuffleHistory.Count - 1;
        }

        /// <summary>切换到下一个播放模式（循环→单曲→随机→循环）。</summary>
        public PlayMode ToggleMode()
        {
            Mode = (PlayMode)(((int)Mode + 1) % 3);
            return Mode;
        }

        /// <summary>清空随机播放轨迹（切换目录时调用）。</summary>
        public void ResetShuffleHistory()
        {
            _shuffleHistory.Clear();
            _shufflePos = -1;
        }

        public void SetCurrent(Track? current) { _current = current; }

        /// <summary>删除或替换列表后剔除不再属于列表的历史项。</summary>
        public void ValidateHistory(IList<Track> tracks)
        {
            for (int i = _shuffleHistory.Count - 1; i >= 0; i--)
            {
                if (tracks.Contains(_shuffleHistory[i])) continue;
                _shuffleHistory.RemoveAt(i);
                if (i <= _shufflePos) _shufflePos--;
            }
            if (_current != null && !tracks.Contains(_current)) _current = null;
        }

        private Track? GetNextInternal(IList<Track> tracks, Track? current)
        {
            if (Mode == PlayMode.Shuffle)
            {
                // 直接在非当前曲目的候选集中均匀取样，不靠有限次数重抽。
                var candidates = new List<Track>();
                foreach (var track in tracks)
                    if (!ReferenceEquals(track, current)) candidates.Add(track);
                return candidates.Count == 0 ? tracks[0] : candidates[_rng.Next(candidates.Count)];
            }
            else
            {
                // 列表循环
                int idx = current != null ? tracks.IndexOf(current) : -1;
                idx = (idx + 1) % tracks.Count;
                return tracks[idx];
            }
        }
    }
}
