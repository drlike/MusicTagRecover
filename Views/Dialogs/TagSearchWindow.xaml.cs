using System.Windows;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 组合标签在线搜索窗口。new TagSearchWindow(file, source, coverService, lyricService,
/// imageCache, settings).ShowDialog()，返回 true 时读取 SelectedResult。
/// </summary>
public partial class TagSearchWindow : Window
{
    private readonly TagSearchViewModel _viewModel;

    /// <summary>对话框返回 true 时为用户选中的搜索结果。</summary>
    public SearchResult? SelectedResult => _viewModel.SelectedReturn;

    public TagSearchWindow(MusicFile file, string source, ICoverService coverService,
        ILyricService lyricService, IImageCache imageCache, ISettingsService settings)
    {
        InitializeComponent();
        _viewModel = new TagSearchViewModel(file, source, coverService, lyricService,
            imageCache, settings);
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
        Owner = Application.Current?.MainWindow;

        var query = string.IsNullOrWhiteSpace(file.Artist)
            ? file.FileName
            : $"{file.Artist} - {file.Title}";
        QueryText.Text = query;
        Title = $"搜索标签 - {query}";
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
