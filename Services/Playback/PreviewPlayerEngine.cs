using System.Diagnostics;
using NAudio.Wave;

namespace MusicTagRecover.Services.Playback;

/// <summary>
/// 预览播放引擎：统一调度解码器（APE 走官方 SDK，其余走 FFmpeg 9）与 NAudio WASAPI 输出。
///
/// 线程模型：
/// - 单解码线程串行调用 <see cref="IPreviewDecoder"/>，产出的音频块写入
///   <see cref="BufferedWaveProvider"/>（达到缓冲上限时等待），视频帧进入有界队列；
/// - UI 线程通过 <see cref="PumpDueVideoFrame"/> 按播放时钟拉取到期的视频帧显示；
/// - 暂停/跳转通过标志位与解码线程协作，Seek 的实际清缓冲+复位操作在解码线程上执行，
///   避免与解码调用并发。
///
/// 事件 <see cref="MediaEnded"/> / <see cref="MediaFailed"/> 在后台线程触发，订阅方需自行切回 UI 线程。
/// </summary>
public sealed class PreviewPlayerEngine : IDisposable
{
    /// <summary>音频预缓冲上限：达到后解码线程暂停喂数据。</summary>
    private static readonly TimeSpan MaxBufferedAudio = TimeSpan.FromSeconds(2);

    /// <summary>视频帧队列容量（满时解码线程等待）。</summary>
    private const int MaxQueuedVideoFrames = 4;

    /// <summary>视频帧迟到容忍：超过该值的帧直接丢弃。</summary>
    private static readonly TimeSpan LateFrameTolerance = TimeSpan.FromMilliseconds(250);

    private readonly PlaybackBufferPool _pool = new();

    private IPreviewDecoder? _decoder;
    private BufferedWaveProvider? _provider;
    private WaveOut? _waveOut;
    private Thread? _decodeThread;

    // ---- 跨线程状态（volatile 标志 + 锁保护数据）----
    private volatile bool _isOpen;
    private volatile bool _closeRequested;
    private volatile bool _paused = true;
    private volatile bool _seekRequested;
    private volatile bool _decoderEof;
    private volatile bool _endedFired;
    private TimeSpan _seekTarget;

    /// <summary>音频时钟锚点：已写入输出缓冲的音频末尾对应的播放时间。</summary>
    private readonly object _audioGate = new();
    private TimeSpan _writtenPtsEnd;

    // 无音频流（纯视频）时使用墙钟计时
    private readonly Stopwatch _wallClock = new();
    private TimeSpan _wallClockOffset;

    // 视频帧队列（解码线程生产，UI 线程消费）
    private readonly object _videoGate = new();
    private readonly Queue<DecodedUnit> _videoQueue = new();

    private double _volume = 0.5;
    private bool _isMuted;

    /// <summary>当前媒体自然结束（仅音频排空后触发一次）。</summary>
    public event EventHandler? MediaEnded;

    /// <summary>后台解码过程中发生不可恢复错误（消息为用户可读文本）。</summary>
    public event EventHandler<string>? MediaFailed;

    // ============================================================
    // 打开 / 关闭
    // ============================================================

    /// <summary>打开媒体文件并启动解码线程；失败抛 <see cref="PlaybackException"/>。</summary>
    public void Open(string path)
    {
        Close();

        // APE 专用解码器（官方 SDK），其余格式统一走 FFmpeg
        IPreviewDecoder decoder = ApeDecoder.IsApeFile(path)
            ? ApeDecoder.IsAvailable
                ? new ApeDecoder(path)
                : throw new PlaybackException(
                    $"缺少 APE 解码组件（{ApeDecoder.DllPath}），请重新安装本软件。")
            : new FfmpegDecoder(path);

        try
        {
            _decoder = decoder;

            if (decoder.HasAudio)
            {
                _provider = new BufferedWaveProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(decoder.SampleRate, decoder.Channels),
                    TimeSpan.FromSeconds(5))
                {
                    DiscardOnBufferOverflow = false,
                    ReadFully = false,
                };
                _waveOut = new WaveOut { BufferMilliseconds = 150, NumberOfBuffers = 3 };
                _waveOut.Init(_provider);
                ApplyVolume();
            }

            _closeRequested = false;
            _seekRequested = false;
            _decoderEof = false;
            _endedFired = false;
            _paused = true;
            lock (_audioGate)
            {
                _writtenPtsEnd = TimeSpan.Zero;
                _wallClockOffset = TimeSpan.Zero;
            }
            _wallClock.Reset();

            _decodeThread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "PreviewDecode",
                Priority = ThreadPriority.AboveNormal,
            };
            _decodeThread.Start();
            _isOpen = true;
        }
        catch
        {
            Close();
            throw;
        }
    }

    /// <summary>关闭当前媒体并释放全部资源（可再次 Open）。</summary>
    public void Close()
    {
        _closeRequested = true;
        lock (_videoGate) Monitor.PulseAll(_videoGate);

        if (_decodeThread != null)
        {
            if (!_decodeThread.Join(TimeSpan.FromSeconds(3)))
                MediaFailed?.Invoke(this, "解码线程未能正常退出。");
            _decodeThread = null;
        }

        try { _waveOut?.Stop(); } catch { /* 忽略 */ }
        try { _waveOut?.Dispose(); } catch { /* 忽略 */ }
        _waveOut = null;
        _provider = null;

        try { _decoder?.Dispose(); } catch { /* 忽略 */ }
        _decoder = null;

        DrainVideoQueue();
        _isOpen = false;
        _paused = true;
    }

    // ============================================================
    // 播放控制
    // ============================================================

    public void Play()
    {
        if (!_isOpen) return;
        if (_endedFired) Seek(TimeSpan.Zero);   // 播完后再次播放：回到开头
        _paused = false;
        _wallClock.Start();
        // 仅当已有缓冲时立即启动输出设备；刚 Open 时缓冲为空，WaveOut 读不到数据会自动停止，
        // 首块音频由解码线程入缓冲后在 FeedAudio 中启动（见 EnsureOutputRunning）
        try
        {
            if (_waveOut != null && _provider is { BufferedBytes: > 0 }
                && _waveOut.PlaybackState != PlaybackState.Playing)
            {
                _waveOut.Play();
            }
        }
        catch { /* 输出设备异常时忽略，由解码循环报错 */ }
    }

    public void Pause()
    {
        _paused = true;
        _wallClock.Stop();
        try { _waveOut?.Pause(); } catch { /* 忽略 */ }
    }

    /// <summary>停止：暂停并回到起点。</summary>
    public void Stop()
    {
        Pause();
        Seek(TimeSpan.Zero);
    }

    /// <summary>请求跳转；实际的解码器 Seek 与缓冲清空在解码线程上执行。</summary>
    public void Seek(TimeSpan position)
    {
        if (!_isOpen) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        var dur = Duration;
        if (dur > TimeSpan.Zero && position > dur) position = dur;
        _seekTarget = position;
        _seekRequested = true;
        lock (_videoGate) Monitor.PulseAll(_videoGate);
    }

    // ============================================================
    // 状态查询
    // ============================================================

    public bool IsOpen => _isOpen;

    public bool IsPlaying => _isOpen && !_paused && !_endedFired;

    public TimeSpan Duration => _decoder?.Duration ?? TimeSpan.Zero;

    /// <summary>当前播放位置：有音频流时以音频输出时钟为准，纯视频以墙钟为准。</summary>
    public TimeSpan Position
    {
        get
        {
            var decoder = _decoder;
            if (decoder == null) return TimeSpan.Zero;
            lock (_audioGate)
            {
                TimeSpan pos;
                if (decoder.HasAudio && _provider != null)
                    pos = _writtenPtsEnd - _provider.BufferedDuration;
                else
                    pos = _wallClockOffset + _wallClock.Elapsed;

                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                var dur = decoder.Duration;
                if (dur > TimeSpan.Zero && pos > dur) pos = dur;
                return pos;
            }
        }
    }

    public bool HasVideo => _decoder?.HasVideo ?? false;
    public int VideoWidth => _decoder?.VideoWidth ?? 0;
    public int VideoHeight => _decoder?.VideoHeight ?? 0;
    public string AudioCodecName => _decoder?.AudioCodecName ?? "";
    public string? VideoCodecName => _decoder?.VideoCodecName;

    /// <summary>音量 0.0 ~ 1.0。</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        try { if (_waveOut != null) _waveOut.Volume = (float)(_isMuted ? 0 : _volume); }
        catch { /* 输出设备异常时忽略 */ }
    }

    // ============================================================
    // 视频帧拉取（UI 线程调用）
    // ============================================================

    /// <summary>
    /// 取出一帧到达显示时间的视频帧；返回 true 时调用方负责显示后调用
    /// <see cref="ReturnFrame"/> 归还缓冲。未到时间的帧继续留在队列，
    /// 迟到超过 <see cref="LateFrameTolerance"/> 的帧直接丢弃（缓冲归还池）。
    /// </summary>
    public bool PumpDueVideoFrame(out DecodedUnit frame)
    {
        frame = null!;
        var now = Position;
        lock (_videoGate)
        {
            while (_videoQueue.Count > 0)
            {
                var head = _videoQueue.Peek();
                if (head.Pts > now) return false;               // 未到显示时间
                _videoQueue.Dequeue();
                if (now - head.Pts > LateFrameTolerance)
                {
                    _pool.Return(head.Buffer);                  // 严重迟到：丢弃
                    continue;
                }
                frame = head;
                return true;
            }
        }
        return false;
    }

    /// <summary>归还视频帧缓冲（与 <see cref="PumpDueVideoFrame"/> 成对调用）。</summary>
    public void ReturnFrame(DecodedUnit frame)
    {
        _pool.Return(frame.Buffer);
        frame.Reset();
    }

    // ============================================================
    // 解码线程
    // ============================================================

    private void DecodeLoop()
    {
        var unit = new DecodedUnit();
        try
        {
            while (!_closeRequested)
            {
                // 跳转请求在解码线程上执行，保证与解码调用不并发
                if (_seekRequested)
                {
                    _pool.Return(unit.Buffer);
                    unit.Reset();
                    ApplySeek();
                    continue;
                }

                // 解码器 EOF：等输出排空后触发 MediaEnded，然后休眠等待跳转/关闭
                if (_decoderEof)
                {
                    SleepAfterEof();
                    continue;
                }

                // 暂停：休眠（保留已缓冲数据，恢复时无缝继续）
                if (_paused)
                {
                    Thread.Sleep(20);
                    continue;
                }

                unit.Reset();
                bool got;
                try
                {
                    got = _decoder!.DecodeNext(unit, _pool.Rent);
                }
                catch (PlaybackException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 解码器内部异常：按不可恢复错误处理
                    throw new PlaybackException("解码过程中发生错误：" + ex.Message, ex);
                }

                if (!got)
                {
                    _decoderEof = true;
                    continue;
                }

                if (unit.IsAudio)
                    FeedAudio(unit);
                else
                    EnqueueVideo(unit);
            }
        }
        catch (Exception ex)
        {
            if (!_closeRequested)
            {
                _decoderEof = true;
                FireMediaFailed(ex.Message);
            }
        }
        finally
        {
            _pool.Return(unit.Buffer);
        }
    }

    /// <summary>把音频块写入输出缓冲；缓冲满时等待（同时响应暂停/跳转/关闭）。</summary>
    private void FeedAudio(DecodedUnit unit)
    {
        var provider = _provider;
        if (provider == null) { _pool.Return(unit.Buffer); return; }

        while (!_closeRequested && !_seekRequested)
        {
            if (_paused) { Thread.Sleep(20); continue; }
            if (provider.BufferedDuration < MaxBufferedAudio) break;
            Thread.Sleep(20);
        }
        if (_closeRequested || _seekRequested) { _pool.Return(unit.Buffer); return; }

        provider.AddSamples(unit.Buffer, 0, unit.Length);

        int bytesPerSecond = _decoder!.SampleRate * _decoder.Channels * sizeof(float);
        lock (_audioGate)
            _writtenPtsEnd = unit.Pts + TimeSpan.FromSeconds(unit.Length / (double)bytesPerSecond);

        EnsureOutputRunning();
        _pool.Return(unit.Buffer);   // AddSamples 已复制数据，缓冲立即可复用
    }

    /// <summary>
    /// 音频入缓冲后确保输出设备处于播放状态。必须在首块数据到达后才 Play：
    /// 缓冲为空时 WaveOut 的读取回调拿到 0 字节会判定播放结束并自动停止，
    /// 这正是“打开/切歌后需再点一次播放”的根因。中途解码欠速导致设备停止时也由此自动恢复。
    /// </summary>
    private void EnsureOutputRunning()
    {
        var waveOut = _waveOut;
        var provider = _provider;
        if (waveOut == null || provider == null || _paused) return;
        try
        {
            if (provider.BufferedBytes > 0 && waveOut.PlaybackState != PlaybackState.Playing)
                waveOut.Play();
        }
        catch { /* 输出设备异常时忽略 */ }
    }

    /// <summary>视频帧入有界队列；满时等待（同时响应暂停/跳转/关闭）。</summary>
    private void EnqueueVideo(DecodedUnit unit)
    {
        lock (_videoGate)
        {
            while (_videoQueue.Count >= MaxQueuedVideoFrames
                   && !_closeRequested && !_seekRequested)
                Monitor.Wait(_videoGate, 20);

            if (_closeRequested || _seekRequested)
            {
                _pool.Return(unit.Buffer);
                return;
            }

            // 单元对象本身不重入池（队列持有），仅缓冲来自池
            _videoQueue.Enqueue(new DecodedUnit
            {
                IsAudio = false,
                Buffer = unit.Buffer,
                Length = unit.Length,
                Width = unit.Width,
                Height = unit.Height,
                Stride = unit.Stride,
                Pts = unit.Pts,
            });
            unit.Buffer = Array.Empty<byte>();   // 所有权已移交队列
        }
    }

    /// <summary>在解码线程上执行跳转：清输出缓冲与视频队列 → 解码器 Seek → 复位时钟锚点。</summary>
    private void ApplySeek()
    {
        _seekRequested = false;
        var target = _seekTarget;

        try { _provider?.ClearBuffer(); } catch { /* 忽略 */ }
        DrainVideoQueue();
        try { _decoder?.Seek(target); }
        catch { /* 某些流不支持精确跳转：忽略，从当前位置继续 */ }

        lock (_audioGate)
        {
            _writtenPtsEnd = target;
            _wallClockOffset = target;
        }
        _wallClock.Reset();
        if (!_paused) _wallClock.Start();

        _decoderEof = false;
        _endedFired = false;
    }

    /// <summary>解码 EOF 后的收尾：等音频排空 → 触发 MediaEnded → 休眠等待跳转/关闭。</summary>
    private void SleepAfterEof()
    {
        // 等待输出缓冲排空（纯视频无音频缓冲，直接视为已排空）
        while (!_closeRequested && !_seekRequested
               && _provider != null && _provider.BufferedBytes > 0)
            Thread.Sleep(50);

        if (!_closeRequested && !_seekRequested && !_endedFired)
        {
            _endedFired = true;
            _wallClock.Stop();
            try { _waveOut?.Stop(); } catch { /* 忽略 */ }
            MediaEnded?.Invoke(this, EventArgs.Empty);
        }

        // 结束后休眠等待外部操作（跳转回开头重新播放等）
        while (!_closeRequested && !_seekRequested && _decoderEof)
            Thread.Sleep(50);
    }

    private void FireMediaFailed(string message)
    {
        if (_endedFired) return;
        _endedFired = true;
        _wallClock.Stop();
        try { _waveOut?.Stop(); } catch { /* 忽略 */ }
        MediaFailed?.Invoke(this, message);
    }

    private void DrainVideoQueue()
    {
        lock (_videoGate)
        {
            while (_videoQueue.Count > 0)
                _pool.Return(_videoQueue.Dequeue().Buffer);
            Monitor.PulseAll(_videoGate);
        }
    }

    public void Dispose() => Close();
}
