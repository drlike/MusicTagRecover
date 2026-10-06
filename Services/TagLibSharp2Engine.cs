using System.Reflection;
using TagLibSharp2.Core;

namespace MusicTagRecover.Services;

/// <summary>
/// TagLibSharp2（NuGet 包 <c>TagLibSharp2</c>）音频标签读写引擎。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>按扩展名分发到各格式的强类型 File（如 <c>MonkeysAudioFile</c>），
/// 不使用内容嗅探工厂，避免“ID3v2 前缀的 APE 被误判为 MP3”之类的边界问题。</item>
/// <item>反射仅用于跨格式统一调度（ReadFromFile/SaveToFile/Ensure*Tag），元数据按类型缓存，
/// 标签字段读写全部走强类型 <see cref="IMediaFile"/>/<see cref="Tag"/>，无 dynamic。</item>
/// <item>该库 0.6.x 对 WAV 的 RIFF INFO 按 Latin-1 写入（中文乱码）、MP3/AIFF 部分文件
/// 不回填音频属性——这些场景由 <see cref="TagService"/> 回退到 TagLib(Sharp 2.3.0) 处理。</item>
/// </list>
/// </para>
/// </summary>
internal static class TagLibSharp2Engine
{
    /// <summary>扩展名 → TagLibSharp2 文件类型全名（程序集名 TagLibSharp2）。</summary>
    private static readonly Dictionary<string, string> FileTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "TagLibSharp2.Mpeg.Mp3File",
        [".flac"] = "TagLibSharp2.Xiph.FlacFile",
        [".ogg"] = "TagLibSharp2.Ogg.OggVorbisFile",
        [".oga"] = "TagLibSharp2.Ogg.OggFlacFile",
        [".oggflac"] = "TagLibSharp2.Ogg.OggFlacFile",
        [".opus"] = "TagLibSharp2.Ogg.OggOpusFile",
        [".m4a"] = "TagLibSharp2.Mp4.Mp4File",
        [".m4b"] = "TagLibSharp2.Mp4.Mp4File",
        [".wma"] = "TagLibSharp2.Asf.AsfFile",
        [".asf"] = "TagLibSharp2.Asf.AsfFile",
        [".aif"] = "TagLibSharp2.Aiff.AiffFile",
        [".aiff"] = "TagLibSharp2.Aiff.AiffFile",
        [".aifc"] = "TagLibSharp2.Aiff.AiffFile",
        [".ape"] = "TagLibSharp2.Ape.MonkeysAudioFile",
        [".wv"] = "TagLibSharp2.WavPack.WavPackFile",
        [".mpc"] = "TagLibSharp2.Musepack.MusepackFile",
        [".mp+"] = "TagLibSharp2.Musepack.MusepackFile",
        [".mpp"] = "TagLibSharp2.Musepack.MusepackFile",
        [".dsf"] = "TagLibSharp2.Dsf.DsfFile",
        [".dff"] = "TagLibSharp2.Dff.DffFile",
    };

    /// <summary>每种文件类型的反射元数据（读方法/保存方法/结果属性）。</summary>
    private sealed class TypeMeta
    {
        public required Type Type { get; init; }
        public required MethodInfo ReadMethod { get; init; }
        public required PropertyInfo ResultIsSuccess { get; init; }
        public required PropertyInfo ResultError { get; init; }
        public required PropertyInfo ResultFile { get; init; }
        public SaveMethodInfo? SaveMethodInfo { get; set; }
        public MethodInfo? EnsureTagMethod { get; set; }
    }

    private static readonly object CacheLock = new();
    private static Dictionary<string, TypeMeta>? _byExt;
    private static Dictionary<Type, TypeMeta>? _byType;

    /// <summary>该扩展名是否由 TagLibSharp2 引擎直接处理。</summary>
    public static bool IsSupported(string extension) => FileTypeNames.ContainsKey(extension);

    private static TypeMeta? GetMeta(string extension)
    {
        EnsureCache();
        return _byExt!.TryGetValue(extension, out var meta) ? meta : null;
    }

    private static TypeMeta? GetMeta(Type type)
    {
        EnsureCache();
        return _byType!.TryGetValue(type, out var meta) ? meta : null;
    }

    private static void EnsureCache()
    {
        if (_byExt != null) return;
        lock (CacheLock)
        {
            if (_byExt != null) return;

            var byExt = new Dictionary<string, TypeMeta>(StringComparer.OrdinalIgnoreCase);
            var byType = new Dictionary<Type, TypeMeta>();
            var assembly = typeof(MediaFile).Assembly;

            foreach (var (ext, typeName) in FileTypeNames)
            {
                var type = assembly.GetType(typeName);
                if (type == null) continue;

                // ReadFromFile(string path, IFileSystem? fs) 或带 validateCrc 第三参的重载
                MethodInfo? readMethod = null;
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ReadFromFile") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 2 || ps[0].ParameterType != typeof(string)) continue;
                    readMethod = m;
                    if (ps.Length == 2) break; // 优先无额外开关的重载
                }
                if (readMethod == null) continue;

                // 各格式 XxxFileReadResult 结构一致（IsSuccess/Error/File），但没有公共接口，按名称取属性
                var resultType = readMethod.ReturnType;
                var meta = new TypeMeta
                {
                    Type = type,
                    ReadMethod = readMethod,
                    ResultIsSuccess = resultType.GetProperty("IsSuccess")!,
                    ResultError = resultType.GetProperty("Error")!,
                    ResultFile = resultType.GetProperty("File")!,
                };
                byExt[ext] = meta;
                byType[type] = meta;
            }

            _byExt = byExt;
            _byType = byType;
        }
    }

    /// <summary>
    /// 读取音频文件标签。成功时返回 <see cref="IMediaFile"/>（实现 IDisposable，用完必须释放）；
    /// 不支持的扩展名/解析失败返回 null。
    /// </summary>
    public static IMediaFile? ReadFile(string path)
    {
        var meta = GetMeta(Path.GetExtension(path));
        if (meta == null) return null;

        var ps = meta.ReadMethod.GetParameters();
        var args = ps.Length == 2
            ? new object?[] { path, null }
            : new object?[] { path, null, false };
        var result = meta.ReadMethod.Invoke(null, args)!;

        if (!(bool)meta.ResultIsSuccess.GetValue(result)!) return null;
        return meta.ResultFile.GetValue(result) as IMediaFile;
    }

    /// <summary>
    /// 确保文件带一个可写标签对象（无标签文件首次写入前调用，如无标签的 APE）。
    /// 返回最新的 <see cref="Tag"/>；无法创建时返回 null。
    /// </summary>
    public static Tag? EnsureTag(IMediaFile mediaFile)
    {
        if (mediaFile.Tag != null) return mediaFile.Tag;

        var meta = GetMeta(mediaFile.GetType());
        if (meta is { EnsureTagMethod: null })
        {
            // Ensure*Tag() 零参方法（各库命名不统一，如 EnsureApeTag）
            meta.EnsureTagMethod = meta.Type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name.StartsWith("Ensure", StringComparison.Ordinal)
                    && m.Name.EndsWith("Tag", StringComparison.Ordinal)
                    && m.GetParameters().Length == 0);
        }
        meta?.EnsureTagMethod?.Invoke(mediaFile, null);
        return mediaFile.Tag;
    }

    /// <summary>保存方法的反射结果：可能是 SaveToFile(string) 或 SaveToFile(string, IFileSystem)。</summary>
    private sealed class SaveMethodInfo
    {
        public required MethodInfo Method { get; init; }
        public required bool TakesFileSystem { get; init; }
    }

    /// <summary>保存对标签的修改；成功返回 true，失败返回 <paramref name="error"/> 描述。</summary>
    public static bool Save(IMediaFile mediaFile, string path, out string? error)
    {
        error = null;
        var meta = GetMeta(mediaFile.GetType());
        if (meta == null) { error = "未找到 TagLibSharp2 类型元数据"; return false; }

        // 各格式保存方法重载不统一：
        //  - 多数格式：SaveToFile(string path)
        //  - DSF 等格式：SaveToFile(string path, IFileSystem fileSystem)（无一参重载），
        //    第二参传 null 即使用默认本地文件系统
        meta.SaveMethodInfo ??= ResolveSaveMethod(meta.Type);
        if (meta.SaveMethodInfo == null) { error = "未找到 SaveToFile 可用重载"; return false; }

        var args = meta.SaveMethodInfo.TakesFileSystem
            ? new object?[] { path, null }
            : new object?[] { path };
        var result = meta.SaveMethodInfo.Method.Invoke(mediaFile, args)!;
        if (result.GetType().GetProperty("IsSuccess")?.GetValue(result) is not bool ok || !ok)
        {
            error = result.GetType().GetProperty("Error")?.GetValue(result) as string;
            return false;
        }
        return true;
    }

    /// <summary>优先一参 SaveToFile(string)；否则取 (string, IFileSystem) 两参重载。</summary>
    private static SaveMethodInfo? ResolveSaveMethod(Type type)
    {
        MethodInfo? twoArg = null;
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "SaveToFile") continue;
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                return new SaveMethodInfo { Method = m, TakesFileSystem = false };
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                twoArg = m;
        }
        return twoArg == null
            ? null
            : new SaveMethodInfo { Method = twoArg, TakesFileSystem = true };
    }

    /// <summary>按指定封面类型构造可写入的封面（各格式写入器只消费 <see cref="IPicture"/> 接口字段）。</summary>
    public static IPicture CreatePicture(byte[] data, string? mimeType, PictureType type, string? description = null)
        => new EnginePicture(mimeType ?? "image/jpeg", type, description ?? string.Empty, data);

    /// <summary><see cref="IPicture"/> 的简单实现，全格式通用。</summary>
    private sealed class EnginePicture : IPicture
    {
        public EnginePicture(string mimeType, PictureType pictureType, string description, byte[] data)
        {
            MimeType = mimeType;
            PictureType = pictureType;
            Description = description;
            PictureData = new BinaryData(data);
        }

        public string MimeType { get; }
        public PictureType PictureType { get; }
        public string Description { get; }
        public BinaryData PictureData { get; }
    }
}
