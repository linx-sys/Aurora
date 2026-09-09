/* ============================================================
 * WaveOutAudioOutput.cs — WaveOutEvent 输出设备适配（IAudioOutput）
 * ============================================================ */
using System;
using NAudio.Wave;

namespace Aurora
{
    public class WaveOutAudioOutput : IAudioOutput
    {
        readonly WaveOutEvent _waveOut;

        public event EventHandler<StoppedEventArgs> PlaybackStopped;

        public WaveOutAudioOutput(ISampleProvider source)
        {
            _waveOut = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 4 };
            _waveOut.PlaybackStopped += (s, e) =>
            {
                var handler = PlaybackStopped;
                if (handler != null) handler(this, e);
            };
            _waveOut.Init(source);
        }

        public float Volume
        {
            get { return _waveOut.Volume; }
            set { _waveOut.Volume = value; }
        }

        public void Play() { _waveOut.Play(); }
        public void Pause() { _waveOut.Pause(); }
        public void Stop() { _waveOut.Stop(); }
        public void Dispose() { try { _waveOut.Dispose(); } catch { } }
    }
}
