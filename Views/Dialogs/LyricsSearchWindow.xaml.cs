using System.Windows;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 歌词在线搜索窗口。new LyricsSearchWindow(file, source, lyricService, tagService, settings).ShowDialog()，
/// 返回 true 且 Saved=true 表示歌词已写入标签，同时可读取 SelectedLyric / SelectedResult。
/// </summary>
public partial class LyricsSearchWindow : Window
{
    private readonly LyricsSearchViewModel _viewModel;

    /// <summary>对话框返回 true 时为选中结果下载到的完整歌词。</summary>
    public LyricInfo? SelectedLyric => _viewModel.SelectedLyric;

    /// <summary>对话框返回 true 时为选中的搜索结果。</summary>
    public SearchResult? SelectedResult => _viewModel.SelectedResult;

    /// <summary>确认时歌词是否已成功写入音频文件标签。</summary>
    public bool Saved => _viewModel.Saved;

    public LyricsSearchWindow(MusicFile file, string source, ILyricService lyricService,
        ITagService tagService, ISettingsService settings)
    {
        InitializeComponent();
        _viewModel = new LyricsSearchViewModel(file, source, lyricService, tagService, settings);
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
        Owner = Application.Current?.MainWindow;

        var query = string.IsNullOrWhiteSpace(file.Artist)
            ? file.FileName
            : $"{file.Artist} - {file.Title}";
        QueryText.Text = query;
        Title = $"搜索歌词 - {query}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.Cancel();
    }

    private void OnRequestClose(bool? dialogResult)
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = dialogResult;
        else
            Close();
    }
}
