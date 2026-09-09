/* ============================================================
 * IPlaybackService.cs — 播放服务接口（P0-2 业务边界）
 * MainViewModel 与 UI 层仅通过此接口与播放引擎交互，
 * PlayerEngine 为唯一实现（后续测试可注入 fake）。
 * ============================================================ */
using System;

namespace Aurora
{
    public interface IPlaybackService : IDisposable
    {
        /// <summary>当前播放状态（Stopped/Playing/Paused）。</summary>
        PlaybackState State { get; }

        /// <summary>主音量（0~1 线性）。</summary>
        float Volume { get; set; }

        /// <summary>播放位置（可写 = 进度跳转）。</summary>
        TimeSpan Position { get; set; }

        /// <summary>当前曲目总时长（无曲目/未知时为 Zero）。</summary>
        TimeSpan Duration { get; }

        /// <summary>当前播放会话 ID（每次 Load/CrossfadeTo 递增；切歌竞态过滤依据）。</summary>
        long SessionId { get; }

        /// <summary>当前加载的文件路径（无则为 null）。</summary>
        string CurrentPath { get; }

        /// <summary>加载曲目（失败返回 false，如解码异常）；成功后位于起点，未自动播放。</summary>
        bool Load(string path);

        /// <summary>跨淡切换：新输入淡入混入，旧输入淡出后移除。</summary>
        bool CrossfadeTo(string path, double fadeSeconds);

        void Play();
        void Pause();
        void Stop();

        /// <summary>进度跳转（内部钳制到 [0, Duration]）。</summary>
        void Seek(TimeSpan position);

        /// <summary>预热下一首解码器（无缝衔接）。</summary>
        void Preload(string path);

        /// <summary>设置 ReplayGain 线性增益（新输入生效）。</summary>
        void SetReplayGain(float linear);

        /// <summary>播放状态变更（后台线程触发，订阅方自行切 UI 线程）。</summary>
        event EventHandler<PlaybackStateChangedEventArgs> StateChanged;

        /// <summary>自然结束（携带会话 ID，供订阅方过滤过期事件）。</summary>
        event EventHandler<PlaybackEndedEventArgs> PlaybackEnded;
    }
}
