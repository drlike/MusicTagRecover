#if NET6_0_OR_GREATER
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MusicTagRecover;

/// <summary>
/// 程序集解析器 — 在模块初始化时注册 libs\ 加载回调。
/// ModuleInitializer 在 Main() 之前运行，确保依赖 DLL 能被正确解析。
/// </summary>
internal static class AssemblyResolver
{
    [ModuleInitializer]
    public static void Initialize()
    {
        AssemblyLoadContext.Default.Resolving += OnResolve;
    }

    private static Assembly? OnResolve(AssemblyLoadContext context, AssemblyName name)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs", name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
#endif
