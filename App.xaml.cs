using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Services;
using MusicTagRecover.Services.Compare;
using MusicTagRecover.ViewModels;
using MusicTagRecover.Views;
using MusicTagRecover.Win32;
using Ffmpeg = FFmpeg.AutoGen.ffmpeg;

namespace MusicTagRecover;

/// <summary>
/// WPF 应用入口：DI 容器、单实例（WM_COPYDATA 传文件路径）、全局异常、启动初始化。
/// </summary>
public partial class App : Application
{
    /// <summary>WM_COPYDATA 标识（与旧版保持一致：'MTag'）</summary>
    public const int CopyDataId = 0x4D546167;

    /// <summary>全局服务容器</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    private ILoggerService? _logger;
    private MainViewModel? _mainViewModel;

    /// <summary>单实例后续进程通过 WM_COPYDATA 传入文件路径时触发（主窗口订阅）。</summary>
    public event Action<IReadOnlyList<string>>? ExternalFilesReceived;

    internal void RaiseExternalFiles(IReadOnlyList<string> paths)
        => ExternalFilesReceived?.Invoke(paths);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已有运行中的实例时，把文件路径通过 WM_COPYDATA 送过去后退出
        if (TryForwardToExistingInstance(e.Args))
        {
            Shutdown();
            return;
        }

        // GBK 等旧代码页支持（歌词编码修正需要）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 全局未处理异常
        _logger = Services.GetRequiredService<ILoggerService>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Load();

        ConfigureFFmpeg();

        // 封面缓存：清理时以标签历史引用的封面为保护集合
        var imageCache = Services.GetRequiredService<IImageCache>();
        var history = Services.GetRequiredService<ITagHistoryService>();
        history.Initialize();
        imageCache.SetReferencedCoverPathsProvider(history.GetAllReferencedCoverPaths);
        TrySweepImageCache(imageCache);

        // 启动主窗口并恢复上次文件列表 / 打开命令行文件
        var mainWindow = Services.GetRequiredService<MainWindow>();
        _mainViewModel = (MainViewModel)mainWindow.DataContext;
        MainWindow = mainWindow;
        mainWindow.Show();
        await _mainViewModel.InitializeAsync(e.Args);
    }

    private static void ConfigureServices(IServiceCollection s)
    {
        // 基础设施
        s.AddSingleton<ISettingsService, SettingsService>();
        s.AddSingleton<ILoggerService, LoggerService>();
        s.AddHttpClient();

        // 业务服务
        s.AddSingleton<IImageCache, ImageCacheService>();
        s.AddSingleton<ITagService, TagService>();
        s.AddSingleton<IFileScannerService, FileScannerService>();
        s.AddSingleton<ILyricService, LyricService>();
        s.AddSingleton<ICoverService, CoverService>();
        s.AddSingleton<WebSearchService>();
        s.AddSingleton<AutoMatchService>();
        s.AddSingleton<FilenameRelationService>();
        s.AddSingleton<ITagHistoryService, TagHistoryService>();

        // 比对模块
        s.AddSingleton<CompareDatabaseService>();
        s.AddSingleton<CompareScanService>();

        // 工作目录历史库（文件菜单：添加目录 / 管理目录）
        s.AddSingleton<DirectoryHistoryService>();

        // ViewModel / View
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<MainWindow>();
    }

    private static void TrySweepImageCache(IImageCache imageCache)
    {
        try { imageCache.Sweep(); }
        catch { /* 缓存清理失败不影响启动 */ }
    }

    /// <summary>
    /// 初始化 FFmpeg.AutoGen 的共享库目录（输出目录下的 ffmpeg\ 子目录，由 csproj 拷贝规则
    /// 保证存在），并做一次加载自检。自检失败只记日志：播放功能降级为打开时报错，不影响启动。
    /// </summary>
    private void ConfigureFFmpeg()
    {
        try
        {
            var nativeDir = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (!System.IO.Directory.Exists(nativeDir))
            {
                _logger?.Warn($"未找到 FFmpeg 原生库目录：{nativeDir}，播放预览功能将不可用");
                return;
            }
            Ffmpeg.RootPath = nativeDir;
            // 加载自检：失败会抛 DllNotFoundException/NotSupportedException
            Ffmpeg.avformat_network_init();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "FFmpeg 原生库加载失败，播放预览功能将不可用");
        }
    }

    // ============================================================
    // 单实例支持
    // ============================================================

    /// <summary>查找同名同路径的已有进程，把命令行文件路径发给它。返回 true 表示已转交。</summary>
    private static bool TryForwardToExistingInstance(IReadOnlyList<string> args)
    {
        try
        {
            var files = args.Where(File.Exists).ToList();
            if (files.Count == 0) return false;

            using var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                try
                {
                    if (p.Id == self.Id) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (!string.Equals(p.MainModule?.FileName,
                            Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!p.MainWindowTitle.StartsWith("MusicTag", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string payload = string.Join('\n', files);
                    var bytes = Encoding.Unicode.GetBytes(payload + '\0');
                    IntPtr unmanaged = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                        var cds = new NativeMethods.COPYDATASTRUCT
                        {
                            dwData = (IntPtr)CopyDataId,
                            cbData = bytes.Length,
                            lpData = payload,
                        };
                        NativeMethods.SendMessage(p.MainWindowHandle,
                            NativeMethods.WM_COPYDATA, IntPtr.Zero, ref cds);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(unmanaged);
                    }
                    return true;
                }
                catch { /* 检查候选进程失败则继续 */ }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { /* 单实例检测失败则直接启动新实例 */ }
        return false;
    }

    // ============================================================
    // 全局异常处理
    // ============================================================

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error(e.Exception, "[未处理异常] UI 线程");
        MessageBox.Show("操作过程中发生错误：\n" + e.Exception.Message,
            "MusicTag", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger?.Error(ex, "[未处理异常] AppDomain (IsTerminating={0})", e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error(e.Exception, "[未处理异常] 后台任务");
        e.SetObserved();
    }
}
