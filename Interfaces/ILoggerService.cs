namespace MusicTagRecover.Interfaces;

/// <summary>
/// 简单文件日志服务，输出到 log/ 目录
/// </summary>
public interface ILoggerService
{
    void Debug(string message);
    void Debug(string format, params object?[] args);
    void Info(string message);
    void Info(string format, params object?[] args);
    void Warn(string message);
    void Error(string message);
    void Error(Exception ex, string message);
    void Error(Exception ex, string format, params object?[] args);
}
