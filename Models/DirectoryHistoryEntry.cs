namespace MusicTagRecover.Models;

/// <summary>用户通过「添加目录」保存的工作目录历史条目（DirectoryHistory.db）。</summary>
public class DirectoryHistoryEntry
{
    public long Id { get; set; }

    /// <summary>目录绝对路径（库内按路径去重，大小写不敏感）。</summary>
    public string Path { get; set; } = "";

    /// <summary>添加时勾选的「包含子文件夹」状态，切换工作目录时沿用。</summary>
    public bool IncludeSubDir { get; set; } = true;

    /// <summary>添加时间（DateTime.UtcNow.Ticks）。</summary>
    public long AddedTicks { get; set; }

    /// <summary>最近一次通过「管理目录」切换到该目录的时间（0=从未）。</summary>
    public long LastUsedTicks { get; set; }
}
