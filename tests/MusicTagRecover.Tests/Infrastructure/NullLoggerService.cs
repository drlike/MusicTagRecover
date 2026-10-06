using MusicTagRecover.Interfaces;

namespace MusicTagRecover.Tests.Infrastructure;

/// <summary>空日志实现（测试只关注操作结果，不关心日志输出）。</summary>
public sealed class NullLoggerService : ILoggerService
{
    public void Debug(string message) { }
    public void Debug(string format, params object?[] args) { }
    public void Info(string message) { }
    public void Info(string format, params object?[] args) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Error(Exception ex, string message) { }
    public void Error(Exception ex, string format, params object?[] args) { }
}
