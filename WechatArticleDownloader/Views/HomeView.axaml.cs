using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WechatArticleDownloader.ViewModels;

namespace WechatArticleDownloader.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private async void BrowseOutputButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输出目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && DataContext is HomeViewModel vm)
        {
            vm.SetOutputDir(folders[0].Path.LocalPath);
        }
    }
}
