using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicTagRecover.ChineseUtils;
using MusicTagRecover.Infrastructure;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;

namespace MusicTagRecover.ViewModels.Dialogs;

/// <summary>
/// 歌词编辑对话框 VM：时间轴处理、空行/头部标签清理、简繁转换、LRC 导入/另存、写入标签。
/// "保存到标签"成功后置 Saved=true 并请求关窗（DialogResult=true）。
/// </summary>
public sealed partial class LyricsEditViewModel : ObservableObject
{
    // 匹配 LRC 头部标签行：ti:/ar:/al:/by:/offset:（行首允许空白）
    private static readonly Regex HeadTagLineRegex =
        new(@"^[ \t]*(?:ti|ar|al|by|offset)\s*:[^\r\n]*(?:\r?\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly MusicFile _file;
    private readonly ILyricService _lyricService;
    private readonly ITagService _tagService;
    private readonly ISettingsService _settings;

    /// <summary>编辑中的歌词文本（多行）。</summary>
    [ObservableProperty] private string _lyricsText;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>是否已成功保存到标签。</summary>
    public bool Saved { get; private set; }

    public event Action<bool?>? RequestClose;

    public LyricsEditViewModel(MusicFile file, ILyricService lyricService, ITagService tagService,
        ISettingsService settings)
    {
        _file = file;
        _lyricService = lyricService;
        _tagService = tagService;
        _settings = settings;
        _lyricsText = file.Lyrics ?? string.Empty;
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveToTagsCommand.NotifyCanExecuteChanged();
        SaveLrcCommand.NotifyCanExecuteChanged();
    }

    private bool CanExecute() => !IsBusy;

    /// <summary>将时间标签重新格式化为标准 [mm:ss.xx]。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void ReformatTimeline()
    {
        LyricsText = _lyricService.ReformatTimetag(LyricsText ?? string.Empty);
        StatusText = "已格式化时间轴。";
    }

    /// <summary>删除全部时间标签，保留歌词文本。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void RemoveTimeline()
    {
        LyricsText = _lyricService.RemoveTimetag(LyricsText ?? string.Empty);
        StatusText = "已删除时间轴。";
    }

    /// <summary>删除空行（仅含空白字符的行）。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void RemoveBlankLines()
    {
        var text = LyricsText ?? string.Empty;
        // 保留原有换行风格：先按行拆分，过滤空白行，再用检测到的换行符拼回
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line));
        LyricsText = string.Join(newline, lines);
        StatusText = "已删除空行。";
    }

    /// <summary>删除 LRC 头部标签行（ti:/ar:/al:/by:/offset:）。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void RemoveHeadTags()
    {
        LyricsText = HeadTagLineRegex.Replace(LyricsText ?? string.Empty, string.Empty).TrimEnd('\r', '\n');
        StatusText = "已删除头部标签。";
    }

    /// <summary>简体转繁体。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void ConvertToTraditional()
    {
        LyricsText = ChineseConverter.SimplifiedToTraditional(LyricsText ?? string.Empty);
        StatusText = "已转换为繁体中文。";
    }

    /// <summary>繁体转简体。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void ConvertToSimplified()
    {
        LyricsText = ChineseConverter.TraditionalToSimplified(LyricsText ?? string.Empty);
        StatusText = "已转换为简体中文。";
    }

    /// <summary>导入本地 .lrc/.txt 文件到编辑框（自动尝试 UTF-8/GBK 解码）。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private void ImportLrc()
    {
        var path = WpfUI.OpenFile("LRC/文本文件|*.lrc;*.txt|所有文件|*.*", "导入歌词文件");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var bytes = File.ReadAllBytes(path);
            LyricsText = DecodeLyricFile(bytes);
            StatusText = $"已导入：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            WpfUI.Error("导入歌词文件失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 另存为 LRC：先用文件夹选择器选目录，再用保存文件对话框确认文件名，
    /// 最终通过 ILyricService.SaveLrcFileAsync 落盘（SaveConfig 取设置中的目录/编码/命名格式）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task SaveLrcAsync()
    {
        // 1) 选择保存目录（单选）
        var defaultDir = string.IsNullOrWhiteSpace(_settings.SaveLrcDirectory)
            ? _file.Directory
            : _settings.SaveLrcDirectory!;
        string? dir = null;
        try
        {
            var picker = WpfUI.PickFolders("选择 LRC 保存目录", allowMultiSelect: false);
            dir = picker.SelectedPaths.FirstOrDefault();
        }
        catch { /* 文件夹选择器异常时退化为默认目录 */ }

        dir = string.IsNullOrWhiteSpace(dir) ? defaultDir : dir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            WpfUI.Warning("未选择保存目录。可先在设置中配置 LRC 默认目录。");
            return;
        }

        // 2) 保存文件对话框确认文件名（默认按设置中的命名格式生成）
        var format = string.IsNullOrWhiteSpace(_settings.SaveLrcFilenameFormat)
            ? "{artist} - {title}.lrc"
            : _settings.SaveLrcFilenameFormat!;
        var suggestedName = BuildFilenameFromFormat(format, _file);

        var targetPath = WpfUI.SaveFile("LRC 歌词文件|*.lrc|文本文件|*.txt|所有文件|*.*",
            suggestedName, "另存 LRC", dir);
        if (string.IsNullOrEmpty(targetPath)) return;

        IsBusy = true;
        StatusText = "正在保存 LRC 文件…";
        try
        {
            var config = new LyricInfo.SaveConfig
            {
                SaveDirectory = Path.GetDirectoryName(targetPath)!,
                FileDefaultEncoding = string.IsNullOrWhiteSpace(_settings.SaveLrcFileDefaultEncoding)
                    ? "utf-8"
                    : _settings.SaveLrcFileDefaultEncoding!,
                // 以用户在保存对话框中确认的文件名为准
                FilenameFormat = Path.GetFileName(targetPath),
            };
            var info = new LyricInfo
            {
                OriginalLyric = LyricsText,
                LrcFormatted = LyricsText,
            };

            var savedPath = await _lyricService.SaveLrcFileAsync(dir, _file, info, config);
            if (string.IsNullOrEmpty(savedPath))
            {
                WpfUI.Error("保存 LRC 文件失败，请检查目录权限与文件名。");
                StatusText = "保存失败。";
            }
            else
            {
                StatusText = $"已保存：{savedPath}";
                WpfUI.Info("LRC 文件已保存：\n" + savedPath);
            }
        }
        catch (Exception ex)
        {
            WpfUI.Error("保存 LRC 文件时发生异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>保存编辑后的歌词到音频文件标签，成功后置 Saved=true 并关窗。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task SaveToTagsAsync()
    {
        IsBusy = true;
        StatusText = "正在写入歌词到标签…";
        try
        {
            var ok = await _tagService.WriteLyricsAsync(_file.FilePath, LyricsText ?? string.Empty);
            if (!ok)
            {
                WpfUI.Warning("歌词写入标签失败，请检查文件是否被占用。");
                StatusText = "写入失败。";
                return;
            }

            Saved = true;
            _file.Lyrics = LyricsText;
            _file.HasLyrics = !string.IsNullOrWhiteSpace(LyricsText);
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            WpfUI.Error("写入歌词到标签时发生异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelClose() => RequestClose?.Invoke(Saved);

    /// <summary>按 {artist}/{title}/{album}/{track} 占位符生成安全文件名。</summary>
    private static string BuildFilenameFromFormat(string format, MusicFile file)
    {
        var name = format
            .Replace("{artist}", file.Artist)
            .Replace("{title}", file.Title)
            .Replace("{album}", file.Album)
            .Replace("{track}", file.Track?.ToString("D2") ?? string.Empty);

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), string.Empty);

        return string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name))
            ? "lyric.lrc"
            : name;
    }

    /// <summary>优先按 BOM 解码，无 BOM 时严格 UTF-8 失败则回退 GBK（936）。</summary>
    private static string DecodeLyricFile(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            // 严格 UTF-8：含非法字节序列时抛异常，再回退本地代码页
            var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(936).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }
    }
}
