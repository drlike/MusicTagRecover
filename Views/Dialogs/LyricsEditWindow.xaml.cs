using System.Windows;
using MusicTagRecover.Interfaces;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels.Dialogs;

namespace MusicTagRecover.Views.Dialogs;

/// <summary>
/// 歌词编辑窗口。new LyricsEditWindow(file, lyricService, tagService, settings).ShowDialog()，
/// 返回 true 且 Saved=true 表示歌词已写入标签。
/// </summary>
public partial class LyricsEditWindow : Window
{
    private readonly LyricsEditViewModel _viewModel;

    /// <summary>是否已成功保存歌词到标签。</summary>
    public bool Saved => _viewModel.Saved;

    public LyricsEditWindow(MusicFile file, ILyricService lyricService, ITagService tagService,
        ISettingsService settings)
    {
        InitializeComponent();
        _viewModel = new LyricsEditViewModel(file, lyricService, tagService, settings);
        _viewModel.RequestClose += OnRequestClose;
        DataContext = _viewModel;
        Owner = Application.Current?.MainWindow;
        Title = $"编辑歌词 - {file.FileName}";
    }

    private void OnRequestClose(bool? dialogResult)
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = dialogResult;
        else
            Close();
    }
}
