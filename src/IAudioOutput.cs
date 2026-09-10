/* ============================================================
 * IAudioOutput.cs — 音频输出设备抽象（P1-4 可测试性）
 * PlayerEngine 通过工厂获取此接口；生产环境为 WaveOut/WASAPI 实现，
 * 测试注入 fake 输出（无声卡可跑）。
 * ============================================================ */
using System;
using NAudio.Wave;

namespace Aurora
{
    public interface IAudioOutput : IDisposable
    {
        /// <summary>主音量（0~1 线性）。</summary>
        float Volume { get; set; }

        /// <summary>
        /// 设备级停止回调（真实设备：主动 Stop/Dispose 时触发；
        /// 自然结束走混音器 MixerInputEnded，与此无关）。
        /// </summary>
        event EventHandler<StoppedEventArgs>? PlaybackStopped;

        void Play();
        void Pause();
        void Stop();
    }
}
