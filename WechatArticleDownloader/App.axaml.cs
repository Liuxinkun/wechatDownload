using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.IO;
using System.Threading.Tasks;
using WechatArticleDownloader.ViewModels;
using WechatArticleDownloader.Views;

namespace WechatArticleDownloader;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 双保险：确保 ReactiveUI 的 UI 调度器指向 Avalonia 的 Dispatcher
        RxApp.MainThreadScheduler = AvaloniaScheduler.Instance;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write("Dispatcher.UnhandledException", e.Exception);
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };

            var autoUrl = AutoTestUrl(desktop.Args);
            if (autoUrl is { Length: > 0 })
            {
                var url = autoUrl;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        vm.Home.Url = url;
                        // 连续三轮：反复触发命令完成/就绪状态切换，验证跨线程 CanExecute 崩溃已修复
                        for (var round = 1; round <= 3; round++)
                        {
                            CrashLog.Write("AutoTest", null, $"第 {round} 次获取信息");
                            var done = new SemaphoreSlim(0);
                            using var sub = vm.Home.FetchInfoCommand.Execute(System.Reactive.Unit.Default)
                                .Subscribe(_ => done.Release(), ex =>
                                {
                                    CrashLog.Write($"AutoTest-Command-{round}", ex);
                                    done.Release();
                                });
                            await done.WaitAsync(TimeSpan.FromSeconds(60));
                            CrashLog.Write("AutoTest", null,
                                $"第 {round} 次完成: loaded={vm.Home.ArticleLoaded} title={vm.Home.Title} cover={(vm.Home.CoverImage != null)} img={vm.Home.ImageCount}");
                        }

                        // 下载冒烟：验证下载命令、进度回调、历史刷新链路
                        CrashLog.Write("AutoTest", null, "开始下载冒烟测试");
                        vm.Home.SetOutputDir(Path.Combine(AppContext.BaseDirectory, "autotest_downloads"));
                        var dDone = new SemaphoreSlim(0);
                        using var dSub = vm.Home.StartDownloadCommand.Execute(System.Reactive.Unit.Default)
                            .Subscribe(_ => dDone.Release(), ex =>
                            {
                                CrashLog.Write("AutoTest-Download", ex);
                                dDone.Release();
                            });
                        await dDone.WaitAsync(TimeSpan.FromSeconds(240));
                        CrashLog.Write("AutoTest", null, $"下载结束: {vm.Home.ProgressText} | {vm.Home.StatusText}");
                    });
                    await Task.Delay(2000);
                    CrashLog.Write("AutoTest", null, "自动测试结束，准备退出（进程存活说明未崩溃）");
                    Environment.Exit(0);
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? AutoTestUrl(string[]? args)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--autotest" && i + 1 < args.Length)
                return args[i + 1];
        return null;
    }
}

/// <summary>崩溃与诊断日志（写入程序目录 crash.log）。</summary>
public static class CrashLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");

    public static void Write(string source, Exception? ex, string? note = null)
    {
        lock (Gate)
        {
            try
            {
                using var w = new StreamWriter(Path, true);
                w.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
                if (note != null) w.WriteLine("  " + note);
                if (ex != null)
                {
                    w.WriteLine("  " + ex.GetType().FullName + ": " + ex.Message);
                    w.WriteLine(ex.StackTrace ?? "  (no stack)");
                }
                w.WriteLine();
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
