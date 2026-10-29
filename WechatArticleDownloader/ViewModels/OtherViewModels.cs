using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using SukiUI;
using SukiUI.Models;
using SukiUI.Toasts;
using WechatArticleDownloader.Models;
using WechatArticleDownloader.Services;
using WechatArticleDownloader.Utils;

namespace WechatArticleDownloader.ViewModels;

public class HistoryViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    private HistoryEntry? _selected;
    public HistoryEntry? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<HistoryEntry?, Unit> OpenFolderCommand { get; }
    public ReactiveCommand<HistoryEntry?, Unit> OpenReportCommand { get; }

    public HistoryViewModel(AppSettings settings)
    {
        _settings = settings;
        RefreshCommand = ReactiveCommand.Create(() => Refresh(_settings.OutputDir));
        var canOpenEntry = this.WhenAnyValue(x => x.Selected)
            .Select(s => s != null)
            .ObserveOn(RxApp.MainThreadScheduler);
        OpenFolderCommand = ReactiveCommand.Create<HistoryEntry?>(OpenFolder, canOpenEntry);
        OpenReportCommand = ReactiveCommand.Create<HistoryEntry?>(OpenReport, canOpenEntry);
    }

    public void Refresh(string outputDir)
    {
        Entries.Clear();
        foreach (var e in HistoryService.Load(outputDir))
            Entries.Add(e);
    }

    private static void OpenFolder(HistoryEntry? entry)
    {
        if (entry != null && Directory.Exists(entry.Directory))
            Process.Start(new ProcessStartInfo { FileName = entry.Directory, UseShellExecute = true });
    }

    private static void OpenReport(HistoryEntry? entry)
    {
        if (entry == null) return;
        var report = Path.Combine(entry.Directory, "index.html");
        if (File.Exists(report))
            Process.Start(new ProcessStartInfo { FileName = report, UseShellExecute = true });
    }
}

public class SettingViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly ISukiToastManager _toast;

    public event Action<string>? OutputDirChanged;

    public SettingViewModel(AppSettings settings, ISukiToastManager toast)
    {
        _settings = settings;
        _toast = toast;
        SaveCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await SettingsService.SaveAsync(_settings);
            OutputDirChanged?.Invoke(_settings.OutputDir);
            _toast.CreateSimpleInfoToast().WithTitle("设置").WithContent("设置已保存").Queue();
        });
        ToggleThemeCommand = ReactiveCommand.Create(() =>
            SukiTheme.GetInstance().SwitchBaseTheme());

        ColorThemes = SukiTheme.GetInstance().ColorThemes;
        SelectedTheme = SukiTheme.GetInstance().ActiveColorTheme;
        this.WhenAnyValue(x => x.SelectedTheme)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => { if (t != null) SukiTheme.GetInstance().ChangeColorTheme(t); });
    }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }

    public IReadOnlyList<SukiColorTheme> ColorThemes { get; }

    private SukiColorTheme? _selectedTheme;
    public SukiColorTheme? SelectedTheme
    {
        get => _selectedTheme;
        set => this.RaiseAndSetIfChanged(ref _selectedTheme, value);
    }

    public string OutputDir
    {
        get => _settings.OutputDir;
        set { _settings.OutputDir = value; this.RaisePropertyChanged(); }
    }

    public int Workers
    {
        get => _settings.Workers;
        set { _settings.Workers = Math.Clamp(value, 1, 16); this.RaisePropertyChanged(); }
    }

    public int DelayMs
    {
        get => _settings.DelayMs;
        set { _settings.DelayMs = Math.Clamp(value, 0, 5000); this.RaisePropertyChanged(); }
    }

    public int Retries
    {
        get => _settings.Retries;
        set { _settings.Retries = Math.Clamp(value, 1, 10); this.RaisePropertyChanged(); }
    }

    public bool OriginalImage { get => _settings.OriginalImage; set { _settings.OriginalImage = value; this.RaisePropertyChanged(); } }
    public bool SaveHtml { get => _settings.SaveHtml; set { _settings.SaveHtml = value; this.RaisePropertyChanged(); } }

    public void SetOutputDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        OutputDir = dir;
    }
}

public class AboutViewModel : ViewModelBase
{
    public string AppName { get; } = "公众号文章资源下载器";
    public string Version { get; } = "v1.0.0";
    public string Description { get; } =
        "一键抓取微信公众号文章中的图片、视频、音频与封面资源，" +
        "支持并发下载、原图模式、断点续传式清单与可视化报告。" +
        "UI 设计借鉴自 LottieViewConvert（Avalonia + SukiUI）。";
}
