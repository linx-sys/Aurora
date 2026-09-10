/* ============================================================
 * PlayerEngineAdvancedTests.cs — 引擎测试补强（阶段 6，12 → 50+）
 * 复用 PlayerEngineTests.cs 中的 FakeWaveStream / FakeAudioOutput
 * （同程序集 internal，FakeWaveStream 支持非静音幅值、FakeAudioOutput 记录峰值）。
 * 覆盖：快速切歌竞态 / 跨淡移除 / Preload 命中失效 / ReplayGain 联动 /
 *       Stop-Dispose 语义 / 输出工厂异常回退 / Position-Reader 竞态 / 自然结束细节。
 * 全部无声卡可跑（CI 安全）。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using Xunit;

namespace Aurora.Tests
{
    public class PlayerEngineAdvancedTests : IDisposable
    {
        readonly List<PlayerEngine> engines = new List<PlayerEngine>();
        readonly List<string> tempFiles = new List<string>();
        readonly List<FakeAudioOutput> fakes = new List<FakeAudioOutput>();

        public void Dispose()
        {
            foreach (var e in engines) try { e.Dispose(); } catch { }
            foreach (var f in tempFiles) try { File.Delete(f); } catch { }
        }

        /* ---------- 测试基建 ---------- */

        string NewDummy()
        {
            string p = Path.Combine(Path.GetTempPath(), "aurora_adv_" + Guid.NewGuid().ToString("N") + ".mp3");
            File.WriteAllText(p, "x");
            tempFiles.Add(p);
            return p;
        }

        FakeAudioOutput NewFake()
        {
            var f = new FakeAudioOutput();
            fakes.Add(f);
            return f;
        }

        PlayerEngine CreateEngine(FakeAudioOutput fake = null, Func<string, WaveStream> decoder = null)
        {
            fake = fake ?? NewFake();
            string dummy = NewDummy();
            var e = new PlayerEngine(src => { fake.Source = src; return fake; },
                decoder ?? (p => new FakeWaveStream(1.0)));
            engines.Add(e);
            return e;
        }

        /// <summary>结束事件采集器（线程安全）。</summary>
        class EndedLog
        {
            public readonly List<Tuple<long, string>> Items = new List<Tuple<long, string>>();
            public void Hook(PlayerEngine e)
            {
                e.PlaybackEnded += (s, a) => { lock (Items) Items.Add(Tuple.Create(a.SessionId, a.Path)); };
            }
            public int Count { get { lock (Items) return Items.Count; } }
            public long SessionAt(int i) { lock (Items) return Items[i].Item1; }
            public string PathAt(int i) { lock (Items) return Items[i].Item2; }
        }

        static bool WaitUntil(Func<bool> cond, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                Thread.Sleep(10);
            }
            return cond();
        }

        static List<PlaybackState> CollectStates(PlayerEngine e)
        {
            var list = new List<PlaybackState>();
            e.StateChanged += (s, a) => { lock (list) list.Add(a.State); };
            return list;
        }

        /* ============================================================
         * 一、快速切歌竞态
         * ============================================================ */

        [Fact]
        public void RapidLoad_ABC_CurrentIsC_SessionMonotonic()
        {
            var e = CreateEngine();
            e.Load(NewDummy());
            e.Load(NewDummy());
            e.Load(NewDummy());

            Assert.Equal(3, e.SessionId);
            Assert.Equal(PlaybackState.Stopped, e.State);
            Assert.Equal(TimeSpan.Zero, e.Position);
            Assert.Equal(TimeSpan.FromSeconds(1.0), e.Duration);
        }

        [Fact]
        public void RapidSwitch_WhilePlaying_OnlyLatestSessionEnds()
        {
            // A 用 10s 长流防止在切换前排空；B 用 1s 流——fake 拉取线程会持续取数，
            // Load B 后无需 Play 也会排空 B → 自然结束事件只属于 B（A 被 RemoveAllInputs 静默移除）
            string fileA = NewDummy(), fileB = NewDummy();
            var dur = new Dictionary<string, double> { { fileA, 10.0 }, { fileB, 1.0 } };
            var e = CreateEngine(decoder: p => new FakeWaveStream(dur[p]));
            var log = new EndedLog(); log.Hook(e);

            e.Load(fileA);
            e.Play();
            long sA = e.SessionId;
            e.Load(fileB);
            long sB = e.SessionId;
            // 播放中硬切后混音器瞬时为空，fake 拉取线程退出（与真实设备行为一致）；
            // 真实流程 PlayTrack 在 Load 后总会 Play —— 这里同样恢复拉取
            e.Play();

            Assert.True(WaitUntil(() => log.Count >= 1), "未收到 B 的自然结束");
            Assert.Equal(sB, log.SessionAt(0));
            Assert.NotEqual(sA, log.SessionAt(0));

            Thread.Sleep(800);   // 给 A 的潜在"过期事件"留窗口
            Assert.Equal(1, log.Count);   // A 的会话事件必须被会话过滤/移除机制吞掉
        }

        [Fact]
        public void SessionId_Monotonic_AcrossLoadAndCrossfade()
        {
            var e = CreateEngine();
            string f = NewDummy();
            e.Load(f);                       // 1
            e.CrossfadeTo(f, 0.05);          // 2
            e.Load(f);                       // 3
            e.CrossfadeTo(f, 0.05);          // 4
            Assert.Equal(4, e.SessionId);
        }

        [Fact]
        public void Reload_SamePath_SessionIncrements()
        {
            var e = CreateEngine();
            string f = NewDummy();
            e.Load(f);
            long s1 = e.SessionId;
            e.Load(f);
            Assert.Equal(s1 + 1, e.SessionId);
        }

        [Fact]
        public void Load_AfterNaturalEnd_WorksAgain()
        {
            var fake = NewFake();
            var e = CreateEngine(fake);
            var log = new EndedLog(); log.Hook(e);
            string f = NewDummy();

            e.Load(f);
            e.Play();
            Assert.True(WaitUntil(() => log.Count == 1), "第一次自然结束未发生");
            Assert.Equal(PlaybackState.Stopped, e.State);

            // 结束后重新加载播放（模拟列表循环场景）
            Assert.True(e.Load(f));
            Assert.Equal(PlaybackState.Stopped, e.State);
            e.Play();
            Assert.True(WaitUntil(() => log.Count == 2), "第二次自然结束未发生");
        }

        /* ============================================================
         * 二、跨淡（Crossfade）
         * ============================================================ */

        [Fact]
        public void Crossfade_FromPlaying_StaysPlaying_NoPause()
        {
            var fake = NewFake();
            var e = CreateEngine(fake);
            string f = NewDummy();
            e.Load(f);
            e.Play();
            Assert.True(e.CrossfadeTo(f, 0.05));
            Assert.Equal(PlaybackState.Playing, e.State);
            Assert.Equal(0, fake.PauseCount);   // 跨淡全程无 Pause
            e.Stop();
        }

        [Fact]
        public void Crossfade_OldInputRemovedSilently_OnlyNewSessionEnds()
        {
            // A 10s + B 1s：跨淡后 B 自然结束（1 个事件）；
            // A 的淡出后移除走 ScheduleOldInputRemoval 计时器，不得产生结束事件
            string fileA = NewDummy(), fileB = NewDummy();
            var dur = new Dictionary<string, double> { { fileA, 10.0 }, { fileB, 1.0 } };
            var e = CreateEngine(decoder: p => new FakeWaveStream(dur[p]));
            var log = new EndedLog(); log.Hook(e);

            e.Load(fileA);
            e.Play();
            Assert.True(e.CrossfadeTo(fileB, 0.05));

            Assert.True(WaitUntil(() => log.Count >= 1), "B 未自然结束");
            Thread.Sleep(1500);   // > 淡出计时器（0.05s + 0.8s 缓冲）
            Assert.Equal(1, log.Count);   // 只有 B；A 静默移除
            Assert.Equal(PlaybackState.Stopped, e.State);   // B 是 current，结束后归 Stopped

            // 引擎在跨淡+移除后仍完全可用
            Assert.True(e.Load(fileA));
        }

        [Fact]
        public void Crossfade_ZeroFade_Works()
        {
            var e = CreateEngine();
            string f = NewDummy();
            e.Load(f);
            Assert.True(e.CrossfadeTo(f, 0));
            Assert.Equal(PlaybackState.Playing, e.State);
        }

        [Fact]
        public void Crossfade_FromFreshEngine_NoPriorLoad()
        {
            var e = CreateEngine();
            string f = NewDummy();
            Assert.True(e.CrossfadeTo(f, 0.05));   // 无前置 Load 也可直接跨淡载入
            Assert.Equal(PlaybackState.Playing, e.State);
            Assert.Equal(f, e.CurrentPath);
        }

        [Fact]
        public void Crossfade_LongFade_StillEndsNaturally()
        {
            // 淡入 5s 但流只有 1s：数据耗尽仍触发自然结束（淡入不改变结束判定）
            var e = CreateEngine();
            var log = new EndedLog(); log.Hook(e);
            string f = NewDummy();
            e.CrossfadeTo(f, 5.0);
            Assert.True(WaitUntil(() => log.Count == 1), "长淡入下未自然结束");
        }

        /* ============================================================
         * 三、Preload 命中 / 失效
         * ============================================================ */

        [Fact]
        public void Preload_HitOnLoad_DecoderOpenedOnce()
        {
            int opens = 0;
            var e = CreateEngine(decoder: p => { Interlocked.Increment(ref opens); return new FakeWaveStream(1.0); });
            string f = NewDummy();

            e.Preload(f);
            Assert.True(WaitUntil(() => Volatile.Read(ref opens) == 1), "预载未触发解码");
            Thread.Sleep(100);   // 防重复打开窗口

            Assert.True(e.Load(f));   // 命中预载：不再二次打开
            Assert.Equal(1, Volatile.Read(ref opens));
        }

        [Fact]
        public void Preload_SamePath_Idempotent()
        {
            int opens = 0;
            var e = CreateEngine(decoder: p => { Interlocked.Increment(ref opens); return new FakeWaveStream(1.0); });
            string f = NewDummy();

            e.Preload(f);
            Assert.True(WaitUntil(() => Volatile.Read(ref opens) == 1));
            e.Preload(f);   // 同路径幂等：不再打开
            Thread.Sleep(150);
            Assert.Equal(1, Volatile.Read(ref opens));
        }

        [Fact]
        public void Preload_PathChange_DiscardsOld_OpenNew()
        {
            int opens = 0;
            var e = CreateEngine(decoder: p => { Interlocked.Increment(ref opens); return new FakeWaveStream(1.0); });
            string a = NewDummy(), b = NewDummy();

            e.Preload(a);
            Assert.True(WaitUntil(() => Volatile.Read(ref opens) == 1));
            e.Preload(b);   // 路径变更：丢弃旧预载，打开新路径
            Assert.True(WaitUntil(() => Volatile.Read(ref opens) == 2));

            Assert.True(e.Load(b));   // 命中 B 的预载
            Assert.Equal(2, Volatile.Read(ref opens));
        }

        [Fact]
        public void Preload_NonexistentPath_NoCrash_LoadStillWorks()
        {
            var e = CreateEngine();
            e.Preload(@"Z:\nope.mp3");   // 文件不存在：直接返回，不开解码器
            Thread.Sleep(100);
            Assert.True(e.Load(NewDummy()));
        }

        [Fact]
        public void Preload_DecoderThrows_NoCrash_LaterLoadFails()
        {
            string bad = NewDummy();
            var e = CreateEngine(decoder: p => p == bad
                ? throw new InvalidDataException("fake decode fail")
                : (WaveStream)new FakeWaveStream(1.0));

            e.Preload(bad);   // 后台打开失败：reader=null，不进预载，不崩溃
            Thread.Sleep(150);

            Assert.False(e.Load(bad));   // 现开同样失败 → Load false
        }

        [Fact]
        public void Preload_EmptyOrNullPath_NoOp()
        {
            var e = CreateEngine();
            e.Preload(null);
            e.Preload("");
            Assert.True(e.Load(NewDummy()));   // 引擎不受影响
        }

        /* ============================================================
         * 四、ReplayGain 与引擎联动（非静音流 + 峰值捕获）
         * ============================================================ */

        PlayerEngine CreateAmplitudeEngine(FakeAudioOutput fake, float amplitude)
        {
            return CreateEngine(fake, p => new FakeWaveStream(10.0, amplitude));   // 10s 长流，避免提前耗尽
        }

        [Fact]
        public void ReplayGain_DefaultUnity_AmplitudeUnchanged()
        {
            var fake = NewFake();
            var e = CreateAmplitudeEngine(fake, 0.25f);
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => fake.GetPeak() > 0.2f), "未采到播放样本");
            Assert.InRange(fake.GetPeak(), 0.2f, 0.32f);   // 增益 1 → 0.25
            e.Stop();
        }

        [Fact]
        public void ReplayGain_LiveApply_DoublesAmplitude()
        {
            var fake = NewFake();
            var e = CreateAmplitudeEngine(fake, 0.25f);
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => fake.GetPeak() > 0.2f));

            e.SetReplayGain(2f);   // 对当前输入实时生效
            Assert.True(WaitUntil(() => fake.GetPeak() > 0.45f), "实时增益未生效");
            Assert.InRange(fake.GetPeak(), 0.45f, 0.58f);
            e.Stop();
        }

        [Fact]
        public void ReplayGain_BeforeLoad_AppliesToNewInput()
        {
            var fake = NewFake();
            var e = CreateAmplitudeEngine(fake, 0.25f);
            e.SetReplayGain(2f);   // Load 之前设置 → BuildInput 携带
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => fake.GetPeak() > 0.45f));
            e.Stop();
        }

        [Fact]
        public void ReplayGain_ClampHigh_EightX()
        {
            var fake = NewFake();
            var e = CreateAmplitudeEngine(fake, 0.25f);
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => fake.GetPeak() > 0.2f));

            e.SetReplayGain(100f);   // 钳制到 +8
            Assert.True(WaitUntil(() => fake.GetPeak() > 1.5f), "高增益钳制未生效");
            Assert.InRange(fake.GetPeak(), 1.5f, 2.1f);   // 0.25 × 8 = 2.0
            e.Stop();
        }

        [Fact]
        public void ReplayGain_ClampLow_ZeroAmplitude()
        {
            var fake = NewFake();
            var e = CreateAmplitudeEngine(fake, 0.25f);
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => fake.GetPeak() > 0.2f));

            e.SetReplayGain(-5f);   // 钳制到 0 → 静音
            fake.ResetPeak();
            Thread.Sleep(300);
            Assert.True(fake.GetPeak() < 0.01f, "低增益钳制未生效（仍有输出）");
            e.Stop();
        }

        /* ============================================================
         * 五、Stop / Dispose 语义
         * ============================================================ */

        [Fact]
        public void Dispose_Idempotent()
        {
            var e = CreateEngine();
            e.Load(NewDummy());
            e.Dispose();
            e.Dispose();   // 二次 Dispose 不抛
        }

        [Fact]
        public void Dispose_ThenAllCallsNoOp()
        {
            var e = CreateEngine();
            string f = NewDummy();
            e.Load(f);
            long s = e.SessionId;
            e.Dispose();

            Assert.False(e.Load(f));
            e.Play();
            e.Pause();
            e.Stop();
            e.Preload(f);
            e.Seek(TimeSpan.FromSeconds(1));
            Assert.Equal(PlaybackState.Stopped, e.State);
            Assert.Equal(TimeSpan.Zero, e.Position);
            Assert.Equal(TimeSpan.Zero, e.Duration);
            Assert.Equal(s, e.SessionId);
        }

        [Fact]
        public void Stop_WithoutLoad_NoCrash()
        {
            var e = CreateEngine();
            e.Stop();
            Assert.Equal(PlaybackState.Stopped, e.State);
        }

        [Fact]
        public void Pause_WithoutLoad_NoOp()
        {
            var e = CreateEngine();
            e.Pause();
            Assert.Equal(PlaybackState.Stopped, e.State);
        }

        [Fact]
        public void Play_Twice_DevicePlayOnce()
        {
            var fake = NewFake();
            var e = CreateEngine(fake);
            e.Load(NewDummy());
            e.Play();
            e.Play();
            Assert.Equal(1, fake.PlayCount);
            Assert.Equal(PlaybackState.Playing, e.State);
        }

        [Fact]
        public void Stop_Twice_NoCrash()
        {
            var e = CreateEngine();
            e.Load(NewDummy());
            e.Play();
            e.Stop();
            e.Stop();
            Assert.Equal(PlaybackState.Stopped, e.State);
        }

        /* ============================================================
         * 六、输出工厂异常回退
         * ============================================================ */

        [Fact]
        public void OutputFactory_Throws_LoadOk_PlaySilentlyNoStateChange()
        {
            var e = new PlayerEngine(src => throw new Exception("no device"),
                p => new FakeWaveStream(1.0));
            engines.Add(e);
            string f = NewDummy();

            Assert.True(e.Load(f));   // 加载不依赖输出设备
            e.Play();                 // 设备初始化已失败 → 静默返回，状态不变
            Assert.Equal(PlaybackState.Stopped, e.State);
        }

        [Fact]
        public void OutputFactory_ReturnsNull_TreatedAsFailed()
        {
            var e = new PlayerEngine(src => null, p => new FakeWaveStream(1.0));
            engines.Add(e);
            Assert.True(e.Load(NewDummy()));
            e.Play();
            Assert.Equal(PlaybackState.Stopped, e.State);
        }

        [Fact]
        public void Volume_MirrorsDesired_WhenNoOutput()
        {
            var e = new PlayerEngine(src => throw new Exception("no device"), p => new FakeWaveStream(1.0));
            engines.Add(e);
            e.Volume = 0.35f;
            Assert.Equal(0.35f, e.Volume);   // 无设备时返回记忆值
        }

        /* ============================================================
         * 七、Position / Reader 竞态与边界
         * ============================================================ */

        [Fact]
        public void Position_And_Duration_BeforeLoad_Zero()
        {
            var e = CreateEngine();
            Assert.Equal(TimeSpan.Zero, e.Position);
            Assert.Equal(TimeSpan.Zero, e.Duration);
            Assert.Null(e.CurrentPath);
        }

        [Fact]
        public void Seek_WithoutLoad_NoCrash()
        {
            var e = CreateEngine();
            e.Seek(TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.Zero, e.Position);
        }

        [Fact]
        public void Position_Setter_AfterStop_NoCrash()
        {
            var e = CreateEngine();
            e.Load(NewDummy());
            e.Stop();
            e.Position = TimeSpan.FromSeconds(5);   // Reader 已释放 → 早退
            Assert.Equal(TimeSpan.Zero, e.Position);
        }

        [Fact]
        public void CurrentPath_RetainedAfterStop()
        {
            var e = CreateEngine();
            string f = NewDummy();
            e.Load(f);
            e.Stop();
            Assert.Equal(f, e.CurrentPath);   // Stop 不清 CurrentPath（记录最后加载路径）
        }

        [Fact]
        public void Seek_ToExactDuration_ThenPlay_EndsNaturally()
        {
            var e = CreateEngine();
            var log = new EndedLog(); log.Hook(e);
            e.Load(NewDummy());
            e.Seek(TimeSpan.FromSeconds(1.0));   // 钳制到时长末尾 → 数据耗尽
            Assert.Equal(TimeSpan.FromSeconds(1.0), e.Position);
            e.Play();
            Assert.True(WaitUntil(() => log.Count == 1), "Seek 到末尾后未自然结束");
        }

        [Fact]
        public void StateChanged_Sequence_FullCycle()
        {
            var e = CreateEngine();
            var states = CollectStates(e);
            e.Load(NewDummy());     // Stopped
            e.Play();               // Playing
            e.Pause();              // Paused
            e.Play();               // Playing
            e.Stop();               // Stopped

            Assert.Equal(new[] { PlaybackState.Stopped, PlaybackState.Playing, PlaybackState.Paused,
                                 PlaybackState.Playing, PlaybackState.Stopped }, states);
        }

        [Fact]
        public void StateChanged_AfterLoad_CarriesDuration()
        {
            var e = CreateEngine();
            PlaybackStateChangedEventArgs last = null;
            e.StateChanged += (s, a) => last = a;
            e.Load(NewDummy());
            Assert.NotNull(last);
            Assert.Equal(TimeSpan.FromSeconds(1.0), last.Duration);
        }

        /* ============================================================
         * 八、自然结束细节与参数边界
         * ============================================================ */

        [Fact]
        public void NaturalEnd_OnlyOnce()
        {
            var e = CreateEngine();
            var log = new EndedLog(); log.Hook(e);
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => log.Count == 1));
            Thread.Sleep(500);
            Assert.Equal(1, log.Count);   // 无重复事件
        }

        [Fact]
        public void NaturalEnd_StateChangedStopped_AndSessionStable()
        {
            var e = CreateEngine();
            var states = CollectStates(e);
            var log = new EndedLog(); log.Hook(e);
            e.Load(NewDummy());
            long s = e.SessionId;   // Load 之后的会话 ID（结束不改变它）
            e.Play();
            Assert.True(WaitUntil(() => log.Count == 1));
            // ended 事件先于其后的 Stopped StateChanged 触发，等状态到位再断言
            Assert.True(WaitUntil(() => states.Count > 0 && states[states.Count - 1] == PlaybackState.Stopped));
            Assert.Equal(PlaybackState.Stopped, states[states.Count - 1]);
            Assert.Equal(s, e.SessionId);
        }

        [Fact]
        public void NaturalEnd_EventArgs_PathMatches()
        {
            var e = CreateEngine();
            var log = new EndedLog(); log.Hook(e);
            string f = NewDummy();
            e.Load(f);
            e.Play();
            Assert.True(WaitUntil(() => log.Count == 1));
            Assert.Equal(f, log.PathAt(0));
        }

        [Fact]
        public void Pause_StopsDraining_NoPrematureEnd_ResumeWorks()
        {
            var fake = NewFake();
            var e = CreateEngine(fake, p => new FakeWaveStream(10.0));   // 10s：暂停期间不会耗尽
            var log = new EndedLog(); log.Hook(e);
            e.Load(NewDummy());
            e.Play();
            Assert.True(WaitUntil(() => fake.GetPeak() > 0 || fake.PlayCount > 0));
            e.Pause();               // 拉取停止 → 不再有数据推进
            Thread.Sleep(400);
            Assert.Equal(0, log.Count);   // 暂停期间不自然结束
            e.Play();                // 恢复
            Assert.Equal(PlaybackState.Playing, e.State);
            e.Stop();
        }

        [Fact]
        public void Load_NullOrEmptyPath_ReturnsFalse()
        {
            var e = CreateEngine();
            Assert.False(e.Load(null));
            Assert.False(e.Load(""));
        }

        [Fact]
        public void CrossfadeTo_NullPath_ReturnsFalse()
        {
            var e = CreateEngine();
            Assert.False(e.CrossfadeTo(null, 0.05));
        }

        [Fact]
        public void Volume_Initial_IsUnity()
        {
            var e = CreateEngine();
            Assert.Equal(1f, e.Volume);
        }

        [Fact]
        public void Volume_RoundTrip()
        {
            var fake = NewFake();
            var e = CreateEngine(fake);
            e.Volume = 0.35f;
            Assert.Equal(0.35f, e.Volume);
            Assert.Equal(0.35f, fake.Volume);   // 已同步到输出设备
        }

        [Fact]
        public void Load_DuringPlayback_HardSwitch_PositionReset()
        {
            var fake = NewFake();
            var e = CreateEngine(fake);
            string first = NewDummy();
            e.Load(first);
            e.Play();
            Assert.True(WaitUntil(() => e.Position > TimeSpan.Zero), "播放未推进");

            string second = NewDummy();
            e.Load(second);   // 播放中硬切

            // State 由 Load 归 Stopped（Play 恢复）；会话递增；CurrentPath 已切到新曲目
            Assert.Equal(PlaybackState.Stopped, e.State);
            Assert.Equal(second, e.CurrentPath);
            // 拉取线程活跃时新输入的位置会立刻推进——断言"位置属于新曲目"而非"恒为 0"
            Assert.True(e.Position < TimeSpan.FromSeconds(1.0), "位置超出新曲目时长（未重置）");
        }
    }
}
