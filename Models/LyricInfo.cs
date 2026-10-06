namespace MusicTagRecover.Models;

/// <summary>
/// 歌词信息，包含原文歌词、翻译歌词和LRC格式
/// </summary>
public class LyricInfo
{
    public string? OriginalLyric { get; set; }
    public string? TranslatedLyric { get; set; }
    public string? LrcFormatted { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceName { get; set; }
    public bool HasTranslation => !string.IsNullOrEmpty(TranslatedLyric);

    /// <summary>
    /// 歌词下载配置
    /// </summary>
    public class DownloadConfig
    {
        public bool DownloadTranslation { get; set; }
        public bool DontDownloadOriginal { get; set; }
        public string LyricFormat { get; set; } = "{artist} - {title}.lrc";
        public bool ReformatTimetag { get; set; } = true;
        public bool RemoveTimetag { get; set; }
        public bool DeleteHeadTag { get; set; }
        public bool DeleteBlankLines { get; set; } = true;
        public string ChineseConvMode { get; set; } = "none"; // none, toTraditional, toSimplified
    }

    /// <summary>
    /// LRC 文件保存配置
    /// </summary>
    public class SaveConfig
    {
        public string SaveDirectory { get; set; } = string.Empty;
        public string FileDefaultEncoding { get; set; } = "utf-8";
        public string FilenameFormat { get; set; } = "{artist} - {title}.lrc";
    }
}
