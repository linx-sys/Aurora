# Aurora 音频管线（AUDIO_PIPELINE）

> 实现见 `src/PlayerEngine.cs`（IPlaybackService 唯一实现）、`src/IAudioOutput.cs`、
> `src/WaveOutAudioOutput.cs` / `src/WasapiAudioOutput.cs`、`src/AudioDecoders.cs`、`src/GainFadeSampleProvider.cs`。

## 管线拓扑

```
IAudioOutput（常驻设备，150ms 延迟 / 4 缓冲）
  WaveOutEvent（默认） 或 WasapiOut(Exclusive)（设置开启时优先，失败自动回退）
  ← SampleCaptureProvider          频谱采样（挂在混音器输出，UI 经 PullSpectrum 拉取）
  ← MixingSampleProvider           48kHz stereo float，ReadFully=false
     ← 曲目输入 ×N
        WaveStream（解码器：MF/NVorbis/Concentus，见 README 格式表）
        → AudioDecoders.NormalizeToMixer   多声道>2 截前二 → 单声道转立体声 → WDL 重采样 48k
        → GainFadeSampleProvider           ReplayGain 增益 × 淡入/淡出包络
```

## 关键语义（改动前必读）

- **设备生命周期**：构造不同步建设备——生产路径后台预热，首次 Play/Pause/Stop 时 `EnsureOutput()` 按需补建（启动提速）；设备初始化失败置 `_outputFailed` 不再重试（播放静音降级，不崩溃）。
- **会话 ID（SessionId）**：每次 Load/CrossfadeTo 递增；所有异步结束事件携带触发源会话，订阅方（MainViewModel.OnPlaybackEnded 第二道防线）过滤过期事件。
- **自然结束** = 混音器 `MixerInputEnded`（非设备 PlaybackStopped）；设备级 Stopped 仅在主动 Stop/Dispose 时发生，引擎直接忽略。
- **预加载（Gapless）**：剩余 <4s（跨淡时 <cf+1.5s）由 MainViewModel.PreloadNextIfNearEnd 调 Preload 预热解码器；手动切歌作废预选。
- **跨淡（Crossfade）**：剩余 ≤cf 秒（设置 0–12s，默认关）时新输入淡入混入、旧输入淡出后定时移除。
- **格式归一**：混音器固定 48kHz stereo float；多声道取前二（不下混矩阵）、单声道转立体声、重采样。
- **ReplayGain 2.0**：详见 ARCHITECTURE.md；懒分析结果写 SQLite，`SetReplayGain` 实时作用于当前输入。

## 可测试性（P1-4）

- `PlayerEngine(Func<ISampleProvider, IAudioOutput> outputFactory, Func<string, WaveStream> decoder)` 注入构造：测试用 FakeAudioOutput（后台线程拉样本模拟设备）+ FakeWaveStream（有限时长可 Seek 流），无声卡覆盖状态机/竞态/自然结束。
- 生产默认工厂 `DefaultCreateOutput`：WASAPI 独占优先，异常回退 WaveOutEvent。
- 测试清单见 `tests/PlayerEngineTests.cs`；基线数量与扩展计划见 docs/ROADMAP.md 阶段 6。

## 频谱（预留）

SampleCaptureProvider 已抓取 PCM 样本（混音器输出），UI 未消费；接入频谱控件只需在渲染层订阅 `SpectrumDataReady` / 定时 `PullSpectrum()`。
