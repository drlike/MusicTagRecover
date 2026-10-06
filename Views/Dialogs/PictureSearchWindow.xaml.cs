using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 封面在线搜索窗口。调用方式：new PictureSearchWindow(file, source, coverService, settings, imageCache)
/// 后 ShowDialog()，返回 true 时读取 SelectedCover / SelectedResult。
/// </summary>
public partial class PictureSearchWindow : Window
{
    private readonly PictureSearchViewModel _viewModel;

    /// <summary>对话框返回 true 时，为用户选定并下载完成的封面；否则为 null。</summary>
    public CoverArt? SelectedCover => _viewModel.SelectedCover;

    /// <summary>对话框返回 true 时，为用户选定的搜索结果；否则为 null。</summary>
    public SearchResult? SelectedResult => _viewModel.SelectedResult;

    public PictureSearchWindow(MusicFile file, string source, ICoverService coverService,
        ISettingsService settings, IImageCache imageCache)
    {
        InitializeComponent();
        _viewModel = new PictureSearchViewModel(file, source, coverService, settings, imageCache);
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
        Owner = Application.Current?.MainWindow;

        QueryText.Text = string.IsNullOrWhiteSpace(file.Artist)
            ? file.FileName
            : $"{file.Artist} - {file.Title}";
        Title = $"搜索封面 - {QueryText.Text}";
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

    /// <summary>双击结果项等同于"下载此封面"。</summary>
    private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem != null
            && _viewModel.ConfirmCommand.CanExecute(null))
        {
            _viewModel.ConfirmCommand.Execute(null);
        }
    }

    private void OnRequestClose(bool? dialogResult)
    {
        // ShowDialog 打开时才允许设置 DialogResult
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = dialogResult;
        else
            Close();
    }
}
