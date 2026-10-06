using System.Windows;
using System.Windows.Controls;
using MusicTagRecover.Models;
using MusicTagRecover.ViewModels;

namespace MusicTagRecover.Views;

/// <summary>
/// 左侧标签编辑面板。封面类型子菜单在此动态构建（17 种类型），
/// "保存修改"按钮通过 SaveClicked 事件交给主窗口执行。
/// </summary>
public partial class TagEditControl : UserControl
{
    /// <summary>点击"保存修改"时触发（主窗口负责收集 DataGrid 选中项）。</summary>
    public event RoutedEventHandler? SaveClicked;

    private bool _pictureMenuBuilt;

    public TagEditControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => _pictureMenuBuilt = false;
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        EnsurePictureTypeMenu();
        base.OnContextMenuOpening(e);
    }

    private void EnsurePictureTypeMenu()
    {
        if (_pictureMenuBuilt || DataContext is not TagEditViewModel vm) return;
        PictureTypeMenu.Items.Clear();
        foreach (var type in TagEditViewModel.MenuTypes)
        {
            var item = new MenuItem
            {
                Header = TagEditViewModel.DescribeType(type),
                Command = vm.SetPictureTypeCommand,
                CommandParameter = type.ToString(),
            };
            PictureTypeMenu.Items.Add(item);
        }
        _pictureMenuBuilt = true;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
        => SaveClicked?.Invoke(this, e);
}
