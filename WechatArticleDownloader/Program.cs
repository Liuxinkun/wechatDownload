using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Threading.Tasks;
using WechatArticleDownloader.Services;

namespace WechatArticleDownloader;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // 隐藏命令行模式：--cli <url> [--download] [--out <dir>]
        if (args.Length >= 2 && args[0] == "--cli")
            return await CliRunner.RunAsync(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // UseReactiveUI 会把 RxApp.MainThreadScheduler 指向 Avalonia 的 UI 调度器，
    // 否则 ReactiveCommand 的 CanExecute 通知会在线程池线程触发，
    // 跨线程访问 Avalonia 控件属性会抛 "Call from invalid thread" 并直接终止进程。
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .UseReactiveUI()
        .LogToTrace();
}
