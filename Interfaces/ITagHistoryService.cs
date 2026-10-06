using MusicTagRecover.Models;

namespace MusicTagRecover.Interfaces;

/// <summary>
/// 标签历史服务 — 管理 tagshistory 数据库的增删查
/// </summary>
public interface ITagHistoryService
{
    /// <summary>初始化数据库（建表、索引、迁移），每次启动时调用</summary>
    void Initialize();

    /// <summary>
    /// 记录一条标签历史（保存前的状态）。
    /// 内部会做历史保留：每个文件最多保留5条（超出则删除最旧的）。
    /// 全部文本字段（含歌词）存 SQLite，封面写入 cache\history\ 目录。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="file">当前 MusicFile 状态（保存前的快照）</param>
    /// <returns>成功写入返回历史记录的 serial，否则 null</returns>
    string? TryAddHistory(string filePath, MusicFile file);

    /// <summary>获取某个文件的历史记录列表</summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="mostRecentFirst">true=最近优先（默认）</param>
    List<TagHistoryRecord> GetHistory(string filePath, bool mostRecentFirst = true);

    /// <summary>读取历史记录关联的封面数据</summary>
    /// <param name="serial">历史记录 serial</param>
    /// <returns>封面 byte[]，无封面或读取失败返回 null</returns>
    byte[]? ReadCoverData(string serial);

    /// <summary>检查历史记录引用的封面文件是否仍然存在；路径无效或检查失败返回 false。</summary>
    bool CoverExists(string? coverPath);

    /// <summary>按 serial 删除单条历史记录（同时删除对应封面文件）</summary>
    void DeleteHistory(string serial);

    /// <summary>删除某个文件的所有历史记录</summary>
    void DeleteHistoryByFilePath(string filePath);

    /// <summary>清空所有标签历史（含封面文件）</summary>
    void ClearAll();

    /// <summary>返回当前所有历史记录引用的 cover_path 集合（去重，已排除 null）。</summary>
    /// <remarks>供 IImageCache 清理孤儿历史封面文件时判断引用关系，避免循环依赖。</remarks>
    IReadOnlyCollection<string> GetAllReferencedCoverPaths();
}
