using MusicTagRecover.Models;

namespace MusicTagRecover.Interfaces;

/// <summary>
/// 标签读写服务接口，支持音频文件元数据的读取、写入和批量操作
/// </summary>
public interface ITagService
{
    /// <summary>从音频文件读取所有标签（含封面字节与歌词正文，编辑单文件时使用）</summary>
    Task<TagData> ReadTagsAsync(string filePath);

    /// <summary>
    /// 轻量读取（目录扫描用）：文本标签/音频属性完整，但不返回封面字节与歌词正文，
    /// 仅提供封面数量（<see cref="TagData.PictureCount"/>）与歌词存在性标志。
    /// </summary>
    Task<TagData> ReadTagsSummaryAsync(string filePath);

    /// <summary>批量读取标签</summary>
    Task<IReadOnlyList<TagData>> ReadTagsBatchAsync(IEnumerable<string> filePaths);

    /// <summary>将标签写入音频文件</summary>
    Task<bool> WriteTagsAsync(string filePath, TagData tags, bool keepUpdateTime = false);

    /// <summary>批量写入标签到多个文件</summary>
    Task<int> WriteTagsBatchAsync(IEnumerable<KeyValuePair<string, TagData>> fileTags,
        bool keepUpdateTime = false, IProgress<int>? progress = null);

    /// <summary>清除标签</summary>
    Task<bool> ClearTagsAsync(string filePath);

    /// <summary>添加封面图片到文件</summary>
    Task<bool> WriteCoverArtAsync(string filePath, CoverArt cover);

    /// <summary>从文件提取封面图片</summary>
    Task<CoverArt?> ReadCoverArtAsync(string filePath);

    /// <summary>写入歌词到文件</summary>
    Task<bool> WriteLyricsAsync(string filePath, string lyrics);

    /// <summary>从文件读取歌词</summary>
    Task<string?> ReadLyricsAsync(string filePath);
}
