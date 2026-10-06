using System.Reflection;

namespace MusicTagRecover;

/// <summary>
/// 应用版本信息
/// </summary>
public static class AppInfo
{
    /// <summary>版本号（例如 0.8.0），从程序集版本读取</summary>
    public static string VersionString { get; } = GetVersionString();

    private static string GetVersionString()
    {
        try
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            if (ver != null)
                return $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
        catch
        {
            // 读取失败时回退到硬编码版本
        }
        return "0.8.1";
    }
}
