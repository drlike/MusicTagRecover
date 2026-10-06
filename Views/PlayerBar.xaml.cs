using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Services.Playback;

namespace MusicTagRecover.Views;

/// <summary>
/// 主窗口内嵌播放器（自研播放内核：FFmpeg 9 覆盖主流音视频格式，
/// APE 走 Monkey's Audio 官方 SDK，NAudio WASAPI 输出，无需系统安装任何解码器）。
/// 控件本身只负责播放与控制；全屏时由 MainWindow 把控件重parent到全屏覆盖层，
/// 并回调 <see cref="ApplyFullscreenLayout"/> 调整内部布局。全程不弹出新窗口。
/// </summary>
public partial class PlayerBar : UserControl
{
    private readonly PreviewPlayerEngine _engine = new();
    private readonly DispatcherTimer _timer;        // 界面刷新（进度/按钮态）
    private readonly DispatcherTimer _videoTimer;   // 视频帧拉取

    /// <summary>串行化“打开/关闭媒体”操作，避免快速切歌时上一个解码流程尚未退出就打开下一个。</summary>
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private IReadOnlyList<string> _playlist = Array.Empty<string>();
    private int _index;
    private bool _scrubbing;
    private WriteableBitmap? _videoBitmap;

    /// <summary>请求 MainWindow 切换全屏（参数为目标状态）。</summary>
    public event EventHandler<bool>? FullscreenRequested;

    /// <summary>用户点击关闭按钮：停止并隐藏播放栏。</summary>
    public event EventHandler? CloseRequested;

    public PlayerBar()
    {
        InitializeComponent();

        _engine.Volume = VolumeSlider.Value / 100d;
        // 引擎事件在后台线程触发，统一切回 UI 线程处理
        _engine.MediaEnded += (_, _) => Dispatcher.BeginInvoke(OnEngineMediaEnded);
        _engine.MediaFailed += (_, msg) => Dispatcher.BeginInvoke(() => OnEngineMediaFailed(msg));

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _videoTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _videoTimer.Tick += (_, _) => PumpVideoFrames();
    }

    /// <summary>以指定播放列表（绝对路径）从 startIndex 开始播放。</summary>
    public async void Play(IReadOnlyList<string> paths, int startIndex = 0)
    {
        if (paths.Count == 0) return;
        _playlist = paths;
        _index = Math.Clamp(startIndex, 0, paths.Count - 1);
        Visibility = Visibility.Visible;
        await OpenCurrentAsync();
    }

    public bool IsMediaOpen => _engine.IsOpen;

    private TimeSpan Duration => _engine.Duration;

    /// <summary>关闭当前媒体（主窗口退出时调用）。</summary>
    public async Task ShutdownAsync()
    {
        _timer.Stop();
        _videoTimer.Stop();
        await _openGate.WaitAsync();
        try
        {
            // 解码线程退出可能涉及原生库调用，放到线程池避免阻塞 UI 关闭流程
            await Task.Run(() => _engine.Dispose());
        }
        catch { /* 退出时忽略 */ }
        finally
        {
            _openGate.Release();
        }
    }

    /// <summary>串行打开当前曲目：同一时刻只允许一个打开/关闭流程操作播放内核。</summary>
    private async Task OpenCurrentAsync()
    {
        await _openGate.WaitAsync();
        try
        {
            await OpenCurrentCoreAsync();
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task OpenCurrentCoreAsync()
    {
        var path = _playlist[_index];

        LoadingOverlay.Visibility = Visibility.Visible;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        AudioPlaceholder.Visibility = Visibility.Collapsed;
        PositionSlider.Value = 0;
        CurrentTimeText.Text = "00:00";
        TotalTimeText.Text = "00:00";

        if (!File.Exists(path))
        {
            ShowError("文件不存在或已被移动/删除", path);
            return;
        }

        try
        {
            // 打开包含解复用与流探测，放线程池避免阻塞界面
            await Task.Run(() => _engine.Open(path));
        }
        catch (Exception ex)
        {
            LogError($"打开媒体失败：{path}", ex);
            ShowError("文件读取失败或格式不受支持",
                $"{Path.GetFileName(path)}\n{ex.Message}");
            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;

        // 音频/视频状态：占位层与视频帧定时器
        if (_engine.HasVideo)
        {
            AudioPlaceholder.Visibility = Visibility.Collapsed;
            _videoTimer.Start();
        }
        else
        {
            _videoTimer.Stop();
            VideoImage.Source = null;
            _videoBitmap = null;
            AudioPlaceholder.Visibility = Visibility.Visible;
        }

        TotalTimeText.Text = FormatTime(Duration);
        PositionSlider.Maximum = Math.Max(1, Duration.TotalMilliseconds);
        UpdateTitle();
        UpdateNavButtons();

        _engine.Play();
        PlayPauseButton.Content = "⏸";
    }

    private void OnEngineMediaEnded()
    {
        if (_index + 1 < _playlist.Count)
        {
            Goto(_index + 1);
        }
        else
        {
            PlayPauseButton.Content = "▶";
            if (!_scrubbing) PositionSlider.Value = PositionSlider.Maximum;
        }
    }

    private void OnEngineMediaFailed(string message)
    {
        var path = _index >= 0 && _index < _playlist.Count ? _playlist[_index] : "";
        LogError($"播放失败：{path}（{message}）", new PlaybackException(message));
        ShowError("播放失败：编码不受支持或文件已损坏", message);
    }

    /// <summary>从引擎拉取到期的视频帧并显示（视频定时器驱动）。</summary>
    private void PumpVideoFrames()
    {
        while (_engine.PumpDueVideoFrame(out var frame))
        {
            try
            {
                if (_videoBitmap == null
                    || _videoBitmap.PixelWidth != frame.Width
                    || _videoBitmap.PixelHeight != frame.Height)
                {
                    _videoBitmap = new WriteableBitmap(
                        frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                    VideoImage.Source = _videoBitmap;
                }
                _videoBitmap.Lock();
                _videoBitmap.WritePixels(
                    new Int32Rect(0, 0, frame.Width, frame.Height),
                    frame.Buffer, frame.Length, frame.Stride);
                _videoBitmap.Unlock();
            }
            catch { /* 单帧显示失败不影响后续播放 */ }
            finally
            {
                _engine.ReturnFrame(frame);
            }
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_engine.IsOpen) return;

        PlayPauseButton.Content = _engine.IsPlaying ? "⏸" : "▶";

        if (Duration != TimeSpan.Zero)
        {
            TotalTimeText.Text = FormatTime(Duration);
            PositionSlider.Maximum = Math.Max(1, Duration.TotalMilliseconds);
        }
        if (!_scrubbing)
        {
            var pos = _engine.Position;
            PositionSlider.Value = Math.Min(pos.TotalMilliseconds, PositionSlider.Maximum);
            CurrentTimeText.Text = FormatTime(pos);
        }
        else
        {
            CurrentTimeText.Text = FormatTime(TimeSpan.FromMilliseconds(PositionSlider.Value));
        }
    }

    private void UpdateTitle()
    {
        if (_index < 0 || _index >= _playlist.Count) return;
        var name = Path.GetFileName(_playlist[_index]);
        TitleText.Text = _playlist.Count > 1 ? $"{name}    （{_index + 1}/{_playlist.Count}）" : name;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(_engine.VideoCodecName)) parts.Add(_engine.VideoCodecName);
        if (_engine.VideoWidth > 0) parts.Add($"{_engine.VideoWidth}×{_engine.VideoHeight}");
        if (!string.IsNullOrEmpty(_engine.AudioCodecName)) parts.Add(_engine.AudioCodecName);
        if (!_engine.HasVideo) parts.Insert(0, "音频");
        CodecText.Text = string.Join("    ", parts);
    }

    private void UpdateNavButtons()
    {
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index + 1 < _playlist.Count;
    }

    private void Goto(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _playlist.Count) return;
        _index = newIndex;
        _ = OpenCurrentAsync();
    }

    // ============================================================
    // 控制按钮
    // ============================================================

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_engine.IsOpen) _engine.Stop();
        PositionSlider.Value = 0;
        CurrentTimeText.Text = "00:00";
        PlayPauseButton.Content = "▶";
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => Goto(_index - 1);
    private void OnNextClick(object sender, RoutedEventArgs e) => Goto(_index + 1);

    public void TogglePlayPause()
    {
        if (!_engine.IsOpen) { if (_playlist.Count > 0) _ = OpenCurrentAsync(); return; }
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Play();
    }

    public void SeekBy(TimeSpan delta)
    {
        if (!_engine.IsOpen) return;
        var target = _engine.Position + delta;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        var duration = Duration;
        if (target > duration) target = duration;
        _engine.Seek(target);
    }

    public void AdjustVolume(double deltaPercent)
        => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + deltaPercent, 0, 100);

    private void OnSeekStarted(object sender, DragStartedEventArgs e) => _scrubbing = true;

    private void OnSeekCompleted(object sender, DragCompletedEventArgs e)
    {
        _scrubbing = false;
        if (_engine.IsOpen) _engine.Seek(TimeSpan.FromMilliseconds(PositionSlider.Value));
    }

    /// <summary>点击进度条任意位置跳转（拖动 native slider 先更新 Value，抬起时统一 seek）。</summary>
    private void OnSeekMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_engine.IsOpen && !_scrubbing)
            _engine.Seek(TimeSpan.FromMilliseconds(PositionSlider.Value));
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _engine.Volume = e.NewValue / 100d;
        MuteButton.Content = e.NewValue <= 0 || _engine.IsMuted ? "🔇" : "🔊";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _engine.IsMuted = !_engine.IsMuted;
        MuteButton.Content = _engine.IsMuted ? "🔇" : "🔊";
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
        => FullscreenRequested?.Invoke(this, !_isFullscreen);

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        try { if (_engine.IsOpen) _engine.Stop(); } catch { /* 忽略 */ }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    // 全屏布局（由 MainWindow 完成重parent后调用）
    // ============================================================

    private bool _isFullscreen;

    public void ApplyFullscreenLayout(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            Grid.SetColumn(MediaBox, 0);
            Grid.SetColumnSpan(MediaBox, 2);
            MediaBox.Width = double.NaN;
            MediaBox.Height = double.NaN;
            MediaBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            MediaBox.VerticalAlignment = VerticalAlignment.Stretch;
            MediaBox.Margin = new Thickness(0);

            Grid.SetColumn(InfoPanel, 0);
            Grid.SetColumnSpan(InfoPanel, 2);
            InfoPanel.VerticalAlignment = VerticalAlignment.Bottom;
            InfoPanel.Margin = new Thickness(48, 0, 48, 18);
            Panel.SetZIndex(InfoPanel, 10);
            InfoPanel.Background = new SolidColorBrush(Color.FromArgb(0xC8, 12, 12, 12));
            InfoPanel.Opacity = 1;
            TextElement.SetForeground(InfoPanel, Brushes.White);
        }
        else
        {
            Grid.SetColumnSpan(MediaBox, 1);
            MediaBox.Width = 300;
            MediaBox.Height = 170;
            MediaBox.HorizontalAlignment = HorizontalAlignment.Left;
            MediaBox.VerticalAlignment = VerticalAlignment.Top;
            MediaBox.Margin = new Thickness(0);

            Grid.SetColumn(InfoPanel, 1);
            Grid.SetColumnSpan(InfoPanel, 1);
            InfoPanel.VerticalAlignment = VerticalAlignment.Stretch;
            InfoPanel.Margin = new Thickness(12, 2, 2, 2);
            Panel.SetZIndex(InfoPanel, 0);
            InfoPanel.Background = null;
            InfoPanel.Opacity = 1;
            InfoPanel.ClearValue(TextElement.ForegroundProperty);
        }
    }

    private void ShowError(string title, string detail)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorText.Text = $"{title}\n{detail}";
        ErrorOverlay.Visibility = Visibility.Visible;
    }

    private static void LogError(string message, Exception ex)
    {
        try
        {
            App.Services.GetService<ILoggerService>()?.Error(ex, message);
        }
        catch { /* 日志不可用时忽略 */ }
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
