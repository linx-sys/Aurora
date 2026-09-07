/* ============================================================
 * Spectro.cs — 真实频谱：WASAPI 环回采集系统音频输出
 * COM 环回失败时自动降级为模拟动画。输出 56 根频段能量条。
 * ============================================================ */
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aurora
{
    public class Spectro
    {
        public const int Bars = 56;
        readonly float[] _latest = new float[Bars];
        readonly object _lock = new object();
        Thread _thread;
        volatile bool _running;
        volatile bool _simulate;
        Random _rng;
        double[] _simState = new double[Bars];

        public void Start()
        {
            if (_running) return;
            _running = true;
            _simulate = false;
            _rng = new Random();
            _thread = new Thread(CaptureLoop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null) { try { _thread.Join(500); } catch { } _thread = null; }
            lock (_lock) for (int i = 0; i < Bars; i++) _latest[i] = 0;
        }

        /// <summary>UI 线程调用，取当前 56 根条（0..1）。</summary>
        public void Read(float[] outBars)
        {
            lock (_lock)
            {
                for (int i = 0; i < Bars && i < outBars.Length; i++) outBars[i] = _latest[i];
            }
        }

        /// <summary>播放状态（模拟模式下用于驱动动画）。</summary>
        public bool Playing;

        /* ---------------- 采集线程 ---------------- */

        void CaptureLoop()
        {
            try { CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED); }
            catch { }

            while (_running)
            {
                try
                {
                    RunSession(); // 正常情况下阻塞在此，直到 Stop/异常
                }
                catch { }
                if (!_running) break;
                // 环回失败（无声卡/独占占用）→ 降级模拟并稍后重试真实采集
                _simulate = true;
                SimulateFill();
                Thread.Sleep(2000);
                _simulate = false;
            }
        }

        void RunSession()
        {
            var en = new MMDeviceEnumeratorComObject();
            var enumr = (IMMDeviceEnumerator)en;
            IMMDevice dev;
            Marshal.ThrowExceptionForHR(enumr.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev));

            object o;
            var iidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            Marshal.ThrowExceptionForHR(dev.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out o));
            var client = (IAudioClient)o;

            IntPtr fmtPtr;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out fmtPtr));
            WavFmt fmt = ReadFmt(fmtPtr);
            int channels = Math.Max(1, fmt.Channels);

            Marshal.ThrowExceptionForHR(client.Initialize(
                AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                2000000 /*200ms*/, 0, fmtPtr, IntPtr.Zero));

            object co;
            var iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            Marshal.ThrowExceptionForHR(client.GetService(ref iidCapture, out co));
            var capture = (IAudioCaptureClient)co;

            Marshal.ThrowExceptionForHR(client.Start());

            var window = new float[FftSize]; // 单声道样本窗口
            int wPos = 0;
            var re = new float[FftSize];
            var im = new float[FftSize];
            var mag = new float[FftSize / 2];

            try
            {
                while (_running)
                {
                    uint packet;
                    capture.GetNextPacketSize(out packet);
                    while (packet > 0)
                    {
                        IntPtr data;
                        uint frames;
                        int flags;
                        long devPos, qpc;
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc));

                        if (data != IntPtr.Zero && (flags & 0x2 /*silent*/) == 0)
                        {
                            int total = (int)frames * channels;
                            if (total <= 4096 * 8)
                            {
                                var buf = new float[total];
                                Marshal.Copy(data, buf, 0, total);
                                for (uint f = 0; f < frames; f++)
                                {
                                    float sum = 0;
                                    for (int c = 0; c < channels; c++) sum += buf[f * channels + c];
                                    window[wPos] = sum / channels;
                                    wPos = (wPos + 1) % FftSize;
                                }
                            }
                        }
                        Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames));
                        capture.GetNextPacketSize(out packet);
                    }

                    if (wPos == 0 || wPos == FftSize - 1) // 有数据流入即做谱
                    {
                        // Hann 窗 + FFT
                        for (int i = 0; i < FftSize; i++)
                        {
                            int idx = (wPos + i) % FftSize;
                            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
                            re[i] = (float)(window[idx] * hann);
                            im[i] = 0;
                        }
                        Fft(re, im);
                        for (int i = 0; i < FftSize / 2; i++)
                            mag[i] = (float)Math.Sqrt(re[i] * re[i] + im[i] * im[i]);

                        MapBands(mag, fmt.SampleRate);
                    }

                    Thread.Sleep(16);
                }
            }
            finally
            {
                try { client.Stop(); } catch { }
                Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(enumr);
                Marshal.FreeCoTaskMem(fmtPtr);
            }
        }

        void SimulateFill()
        {
            while (_running && _simulate)
            {
                lock (_lock)
                {
                    for (int i = 0; i < Bars; i++)
                    {
                        double center = 0.35 + 0.25 * Math.Sin(_rng.NextDouble() * Math.PI);
                        double baseV = Math.Exp(-Math.Pow((i / (double)Bars - center) * 2.2, 2));
                        double v = Playing ? baseV * (0.4 + _rng.NextDouble() * 0.6) : 0;
                        _simState[i] = Math.Max(v, _simState[i] * 0.82);
                        _latest[i] = (float)Math.Min(1, _simState[i]);
                    }
                }
                Thread.Sleep(33);
            }
        }

        /* ---------------- 频段映射 ---------------- */

        const int FftSize = 2048;

        void MapBands(float[] mag, int sampleRate)
        {
            var next = new float[Bars];
            double minF = 30, maxF = Math.Min(16000, sampleRate / 2.0);
            for (int i = 0; i < Bars; i++)
            {
                double f0 = minF * Math.Pow(maxF / minF, (double)i / Bars);
                double f1 = minF * Math.Pow(maxF / minF, (double)(i + 1) / Bars);
                int b0 = Math.Min(mag.Length - 1, (int)(f0 / sampleRate * FftSize));
                int b1 = Math.Max(b0 + 1, Math.Min(mag.Length - 1, (int)(f1 / sampleRate * FftSize)));
                float peak = 0;
                for (int b = b0; b <= b1; b++) if (mag[b] > peak) peak = mag[b];
                double db = 20 * Math.Log10(peak + 1e-9);
                next[i] = (float)Math.Max(0, Math.Min(1, (db + 52) / 46));
            }
            lock (_lock)
            {
                for (int i = 0; i < Bars; i++)
                {
                    float cur = _latest[i];
                    float target = next[i];
                    _latest[i] = target > cur ? target : cur * 0.86f;
                }
            }
        }

        /* ---------------- FFT（radix-2 迭代） ---------------- */

        static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float tr = re[b] * cr - im[b] * ci;
                        float ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }

        /* ---------------- WAVEFORMATEX ---------------- */

        struct WavFmt { public int Channels, SampleRate; }

        static WavFmt ReadFmt(IntPtr p)
        {
            // WAVEFORMATEX: tag(2) ch(2) rate(4) avg(4) align(2) bits(2) cb(2)
            short tag = Marshal.ReadInt16(p, 0);
            short ch = Marshal.ReadInt16(p, 2);
            int rate = Marshal.ReadInt32(p, 4);
            if ((ushort)tag == 0xFFFE) // EXTENSIBLE：共享混音格式必为 float32
            {
                short validBits = Marshal.ReadInt16(p, 18);
                if (validBits != 32) { /* 仍按 float 处理，混音格式实际均为 32f */ }
            }
            return new WavFmt { Channels = ch, SampleRate = rate };
        }

        /* ---------------- COM / 常量 ---------------- */

        const int COINIT_MULTITHREADED = 0;
        const int CLSCTX_ALL = 23;
        const int AUDCLNT_SHAREMODE_SHARED = 0;
        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr reserved, int coInit);

        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr p);

        enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            int OpenPropertyStore(int stgmAccess, out IntPtr store);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
            int GetBufferSize(out uint bufferFrames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out uint padding);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            int GetMixFormat(out IntPtr format);
            int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr eventHandle);
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out uint frames, out int flags, out long devicePosition, out long qpcPosition);
            int ReleaseBuffer(uint frames);
            int GetNextPacketSize(out uint frames);
        }
    }
}
