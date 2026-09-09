/* ============================================================
 * WasapiAudioOutput.cs — WASAPI 独占输出设备适配（IAudioOutput）
 * 初始化失败（设备不支持独占等）由构造抛异常，调用方回退 WaveOut。
 * ============================================================ */
using System;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Aurora
{
    public class WasapiAudioOutput : IAudioOutput
    {
        readonly WasapiOut _wasapi;

        public event EventHandler<StoppedEventArgs> PlaybackStopped;

        public WasapiAudioOutput(ISampleProvider source)
        {
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _wasapi = new WasapiOut(device, AudioClientShareMode.Exclusive, false, 150);
            _wasapi.Init(source);
        }

        public float Volume
        {
            get { return _wasapi.Volume; }
            set { _wasapi.Volume = value; }
        }

        public void Play() { _wasapi.Play(); }
        public void Pause() { _wasapi.Pause(); }
        public void Stop() { _wasapi.Stop(); }
        public void Dispose() { try { _wasapi.Dispose(); } catch { } }
    }
}
