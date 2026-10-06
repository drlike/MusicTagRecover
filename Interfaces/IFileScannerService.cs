using MusicTagRecover.Models;

namespace MusicTagRecover.Interfaces;

/// <summary>
/// 文件扫描与操作服务，支持目录扫描、过滤、排序和批量操作
/// </summary>
public interface IFileScannerService
{
    /// <summary>扫描目录获取音乐文件列表</summary>
    Task<IReadOnlyList<MusicFile>> ScanDirectoryAsync(string directory, bool includeSubDirs = true,
        IProgress<int>? progress = null);

    /// <summary>添加单个文件到列表</summary>
    MusicFile? AddFile(string filePath);

    /// <summary>按关键字、类型、时长等条件过滤文件列表</summary>
    IReadOnlyList<MusicFile> FilterFiles(IEnumerable<MusicFile> files,
        string? keyword = null, string? typeFilter = null,
        bool? filterByDuration = null, bool? ignoreVideo = null);

    /// <summary>按指定字段和方向排序文件列表</summary>
    IReadOnlyList<MusicFile> SortFiles(IEnumerable<MusicFile> files,
        string sortField = "FileName", bool ascending = true);

    /// <summary>批量删除文件</summary>
    Task<int> DeleteFilesAsync(IEnumerable<MusicFile> files, IProgress<int>? progress = null);

    /// <summary>批量重命名文件</summary>
    Task<int> RenameFilesAsync(IEnumerable<MusicFile> files,
        Func<MusicFile, string> nameGenerator, IProgress<int>? progress = null);

    /// <summary>获取支持的文件扩展名</summary>
    ISet<string> GetSupportedExtensions();

    /// <summary>检查是否为支持的音频文件</summary>
    bool IsSupportedFile(string filePath);
}
