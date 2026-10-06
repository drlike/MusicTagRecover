using System.Windows;
using MusicTagRecover.Interfaces;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 应用设置窗口。new SettingsWindow(settings, imageCache, history).ShowDialog()，
/// 返回 true 表示用户点击确定且已持久化。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(ISettingsService settings, IImageCache imageCache,
        ITagHistoryService history)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(settings, imageCache, history);
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
        Owner = Application.Current?.MainWindow;
    }

    private void OnRequestClose(bool? dialogResult)
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = dialogResult;
        else
            Close();
    }
}
