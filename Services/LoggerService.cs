using MusicTagRecover.Interfaces;

namespace MusicTagRecover.Services;

/// <summary>
/// 文件日志服务。按天轮转，线程安全，支持 DEBUG/INFO/WARN/ERROR 四级。
/// 日志路径：{AppDir}/log/log-YYYY-MM-DD.log
/// </summary>
public class LoggerService : ILoggerService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly string _logDir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _currentDate;
    private string? _lastLogLevel;
    private string? _lastLogFilePath;

    private static readonly Dictionary<string, int> LevelPriority = new()
    {
        ["debug"] = 0,
        ["info"] = 1,
        ["warn"] = 2,
        ["error"] = 3
    };

    public LoggerService(ISettingsService settings)
    {
        _settings = settings;
        _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
        Directory.CreateDirectory(_logDir);
    }

    public void Debug(string message) => Write("DEBUG", message);

    public void Debug(string format, params object?[] args) =>
        Write("DEBUG", string.Format(format, args));

    public void Info(string message) => Write("INFO", message);

    public void Info(string format, params object?[] args) =>
        Write("INFO", string.Format(format, args));

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(Exception ex, string message) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    public void Error(Exception ex, string format, params object?[] args) =>
        Error(ex, string.Format(format, args));

    private void Write(string level, string message)
    {
        // 检查日志是否启用
        if (!_settings.LogEnabled) return;

        // 检查日志级别
        var configLevel = _settings.LogLevel?.ToLowerInvariant() ?? "debug";
        if (!LevelPriority.TryGetValue(configLevel, out var minPriority))
            minPriority = 0;
        if (!LevelPriority.TryGetValue(level.ToLowerInvariant(), out var msgPriority))
            msgPriority = 0;
        if (msgPriority < minPriority) return;

        var now = DateTime.Now;
        var dateKey = now.ToString("yyyy-MM-dd");
        var timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        // 按行分割多行消息，每行都加时间戳和级别前缀
        var lines = message.Replace("\r\n", "\n").Split('\n');
        var formatted = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (formatted.Length > 0)
                formatted.AppendLine();
            formatted.Append($"[{timestamp}] [{level}] {line}");
        }

        lock (_lock)
        {
            try
            {
                // 确定日志文件路径
                var filePath = _settings.LogFilePath;
                if (string.IsNullOrEmpty(filePath))
                {
                    // 默认按天轮转
                    filePath = Path.Combine(_logDir, $"log-{dateKey}.log");
                }

                // 检查配置是否变化，如果变化则重新创建 writer
                var currentLogLevel = _settings.LogLevel?.ToLowerInvariant() ?? "debug";
                var currentLogFilePath = _settings.LogFilePath;
                if (_writer == null || _currentDate != dateKey ||
                    _lastLogLevel != currentLogLevel ||
                    _lastLogFilePath != currentLogFilePath)
                {
                    _writer?.Dispose();
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    _writer = new StreamWriter(filePath, append: true)
                        { AutoFlush = true };
                    _currentDate = dateKey;
                    _lastLogLevel = currentLogLevel;
                    _lastLogFilePath = currentLogFilePath;
                }

                _writer.WriteLine(formatted.ToString());
            }
            catch
            {
                // 日志本身出错不做任何事，避免递归
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
            _currentDate = null;
        }
    }
}
