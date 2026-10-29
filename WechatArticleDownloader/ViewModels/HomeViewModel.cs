using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Material.Icons;
using ReactiveUI;
using SukiUI;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using WechatArticleDownloader.Models;
using WechatArticleDownloader.Services;
using WechatArticleDownloader.Utils;

namespace WechatArticleDownloader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public ISukiToastManager ToastManager { get; } = new SukiToastManager();
    public ISukiDialogManager DialogManager { get; } = new SukiDialogManager();

    public HomeViewModel Home { get; }
    public HistoryViewModel History { get; }
    public SettingViewModel Settings { get; }
    public AboutViewModel About { get; } = new();

    public ReactiveCommand<Unit, Unit> ToggleBaseThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenRepoCommand { get; }

    public string BaseThemeName => SukiTheme.GetInstance().ActiveBaseTheme == ThemeVariant.Light ? "浅色" : "深色";

    public MainWindowViewModel()
    {
        var settings = SettingsService.Load();
        var articleService = new WechatArticleService();
        var downloader = new DownloadService(articleService);

        Home = new HomeViewModel(settings, articleService, downloader, ToastManager);
        History = new HistoryViewModel(settings);
        Settings = new SettingViewModel(settings, ToastManager);

        Home.HistoryChanged += () => History.Refresh(settings.OutputDir);
        Settings.OutputDirChanged += dir => History.Refresh(dir);

        ToggleBaseThemeCommand = ReactiveCommand.Create(() =>
        {
            SukiTheme.GetInstance().SwitchBaseTheme();
            this.RaisePropertyChanged(nameof(BaseThemeName));
        });

        OpenRepoCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/SwaggyMacro/LottieViewConvert",
                    UseShellExecute = true,
                });
            }
            catch { /* 忽略 */ }
        });

        History.Refresh(settings.OutputDir);
    }
}

public class HomeViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly WechatArticleService _article;
    private readonly DownloadService _downloader;
    private readonly ISukiToastManager _toast;
    private ArticleInfo? _info;
    private CancellationTokenSource? _downloadCts;

    public event Action? HistoryChanged;

    public HomeViewModel(AppSettings settings, WechatArticleService article,
        DownloadService downloader, ISukiToastManager toast)
    {
        _settings = settings;
        _article = article;
        _downloader = downloader;
        _toast = toast;

        // canExecute 必须调度到 UI 线程，否则 CanExecuteChanged 会在线程池线程触发导致跨线程崩溃
        var canFetch = this.WhenAnyValue(x => x.IsBusy, b => !b)
            .ObserveOn(RxApp.MainThreadScheduler);
        var canCancel = this.WhenAnyValue(x => x.IsDownloading)
            .ObserveOn(RxApp.MainThreadScheduler);

        FetchInfoCommand = ReactiveCommand.CreateFromTask(FetchInfoAsync, canFetch);
        StartDownloadCommand = ReactiveCommand.CreateFromTask(StartDownloadAsync, canFetch);
        CancelDownloadCommand = ReactiveCommand.Create(() => _downloadCts?.Cancel(), canCancel);
        OpenOutputCommand = ReactiveCommand.Create(OpenOutput);
        OpenArticleCommand = ReactiveCommand.Create(OpenArticle);
        ClearLogCommand = ReactiveCommand.Create(() => LogLines.Clear());

        // 命令异常兜底：避免异常冒泡到 UI 线程导致进程直接终止
        FetchInfoCommand.ThrownExceptions.Subscribe(ex => OnCommandError("获取信息", ex));
        StartDownloadCommand.ThrownExceptions.Subscribe(ex => OnCommandError("下载", ex));
        CancelDownloadCommand.ThrownExceptions.Subscribe(ex => OnCommandError("取消", ex));
        OpenOutputCommand.ThrownExceptions.Subscribe(ex => OnCommandError("打开目录", ex));
        OpenArticleCommand.ThrownExceptions.Subscribe(ex => OnCommandError("打开原文", ex));
    }

    private void OnCommandError(string scope, Exception ex)
    {
        Log($"{scope}异常：{ex.Message}");
        CrashLog.Write($"Command({scope})", ex);
        try { Warn(ex.Message); } catch { /* 忽略 */ }
    }

    // ---- 状态 ----

    private string _url = "";
    public string Url { get => _url; set => this.RaiseAndSetIfChanged(ref _url, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private bool _isDownloading;
    public bool IsDownloading { get => _isDownloading; set => this.RaiseAndSetIfChanged(ref _isDownloading, value); }

    private bool _articleLoaded;
    public bool ArticleLoaded
    {
        get => _articleLoaded;
        set { if (this.RaiseAndSetIfChanged(ref _articleLoaded, value)) this.RaisePropertyChanged(nameof(ShowEmptyState)); }
    }

    private bool _isLoadingArticle;
    public bool IsLoadingArticle
    {
        get => _isLoadingArticle;
        set { if (this.RaiseAndSetIfChanged(ref _isLoadingArticle, value)) this.RaisePropertyChanged(nameof(ShowEmptyState)); }
    }

    /// <summary>既未加载文章也不在抓取中时展示空态。</summary>
    public bool ShowEmptyState => !_articleLoaded && !_isLoadingArticle;

    private string _title = "";
    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }

    private string _account = "";
    public string Account { get => _account; set => this.RaiseAndSetIfChanged(ref _account, value); }

    private string _publishTime = "";
    public string PublishTime { get => _publishTime; set => this.RaiseAndSetIfChanged(ref _publishTime, value); }

    private Bitmap? _coverImage;
    public Bitmap? CoverImage { get => _coverImage; set => this.RaiseAndSetIfChanged(ref _coverImage, value); }

    private string _statusText = "等待输入链接";
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private int _imageCount;
    public int ImageCount { get => _imageCount; set => this.RaiseAndSetIfChanged(ref _imageCount, value); }

    private int _videoCount;
    public int VideoCount { get => _videoCount; set => this.RaiseAndSetIfChanged(ref _videoCount, value); }

    private int _audioCount;
    public int AudioCount { get => _audioCount; set => this.RaiseAndSetIfChanged(ref _audioCount, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => this.RaiseAndSetIfChanged(ref _progressValue, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => this.RaiseAndSetIfChanged(ref _progressText, value); }

    public ObservableCollection<string> LogLines { get; } = new();

    // ---- 选项（直写 settings）----

    public string OutputDir
    {
        get => _settings.OutputDir;
        set { _settings.OutputDir = value; this.RaisePropertyChanged(); }
    }

    public bool DownloadImages { get => _settings.DownloadImages; set { _settings.DownloadImages = value; this.RaisePropertyChanged(); } }
    public bool DownloadVideos { get => _settings.DownloadVideos; set { _settings.DownloadVideos = value; this.RaisePropertyChanged(); } }
    public bool DownloadAudios { get => _settings.DownloadAudios; set { _settings.DownloadAudios = value; this.RaisePropertyChanged(); } }
    public bool DownloadCover { get => _settings.DownloadCover; set { _settings.DownloadCover = value; this.RaisePropertyChanged(); } }
    public bool OriginalImage { get => _settings.OriginalImage; set { _settings.OriginalImage = value; this.RaisePropertyChanged(); } }
    public bool SaveHtml { get => _settings.SaveHtml; set { _settings.SaveHtml = value; this.RaisePropertyChanged(); } }

    public int Workers
    {
        get => _settings.Workers;
        set { _settings.Workers = Math.Clamp(value, 1, 16); this.RaisePropertyChanged(); }
    }

    // ---- 命令 ----

    public ReactiveCommand<Unit, Unit> FetchInfoCommand { get; }
    public ReactiveCommand<Unit, Unit> StartDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDownloadCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenOutputCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenArticleCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLogCommand { get; }

    /// <summary>供自动化冒烟测试直接调用（与命令同一逻辑）。</summary>
    public Task FetchInfoForTestAsync() => FetchInfoAsync();

    private async Task FetchInfoAsync()
    {
        var url = Url.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Warn("请输入有效的文章链接");
            return;
        }
        IsBusy = true;
        IsLoadingArticle = true;
        ArticleLoaded = false;
        CoverImage = null;
        StatusText = "正在抓取文章…";
        Log("开始抓取文章…");
        try
        {
            var (html, finalUrl) = await _article.FetchArticleAsync(url);
            StatusText = "正在解析资源…";
            _info = _article.ParseMeta(html, url, finalUrl);
            Title = _info.Title;
            Account = _info.Account;
            PublishTime = _info.PublishTime;

            var images = _article.ParseImages(html);
            var videos = _article.ParseVideos(html);
            var audios = _article.ParseAudios(html);
            _pendingImages = images;
            _pendingVideos = videos;
            _pendingAudios = audios;
            ImageCount = images.Count;
            VideoCount = videos.Count;
            AudioCount = audios.Count;

            ArticleLoaded = true;
            Log($"解析成功：《{Title}》 公众号「{Account}」");
            Log($"资源发现：图片 {ImageCount} | 视频 {VideoCount} | 音频 {AudioCount}");

            StatusText = $"共发现 {ImageCount + VideoCount + AudioCount} 个资源";

            // 封面预览：后台解码，失败不影响主流程
            if (!string.IsNullOrEmpty(_info.CoverUrl))
            {
                try
                {
                    var bytes = await _article.DownloadCoverAsync(_info.CoverUrl);
                    if (bytes is { Length: > 0 })
                    {
                        var bmp = await Task.Run(() =>
                        {
                            using var ms = new MemoryStream(bytes);
                            return new Bitmap(ms);
                        });
                        CoverImage = bmp;
                    }
                }
                catch (Exception ex)
                {
                    Log($"封面预览加载失败：{ex.Message}");
                }
            }
            _ = SettingsService.SaveAsync(_settings);
        }
        catch (OperationCanceledException) { StatusText = "已取消"; }
        catch (Exception ex)
        {
            StatusText = "抓取失败";
            Log($"抓取失败：{ex.Message}");
            Warn(ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsLoadingArticle = false;
        }
    }

    private List<ResourceItem> _pendingImages = new();
    private List<ResourceItem> _pendingVideos = new();
    private List<ResourceItem> _pendingAudios = new();

    private async Task StartDownloadAsync()
    {
        if (_info == null)
        {
            Warn("请先获取文章信息");
            return;
        }

        var resources = new List<ResourceItem>();
        if (DownloadCover && !string.IsNullOrEmpty(_info.CoverUrl))
            resources.Add(new ResourceItem { Kind = ResourceKind.Image, Url = _info.CoverUrl, Origin = "cover" });
        if (DownloadImages) resources.AddRange(_pendingImages);
        if (DownloadVideos) resources.AddRange(_pendingVideos);
        if (DownloadAudios) resources.AddRange(_pendingAudios);

        if (resources.Count == 0)
        {
            Warn("没有可下载的资源，请勾选资源类型");
            return;
        }

        IsBusy = true;
        IsDownloading = true;
        ProgressValue = 0;
        ProgressText = "准备下载…";
        StatusText = "正在下载…";
        _downloadCts = new CancellationTokenSource();
        Log($"开始下载 {resources.Count} 个资源（并发 {Workers}）…");

        var uiProgress = new Progress<DownloadProgress>(p =>
        {
            try
            {
                ProgressValue = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                ProgressText = $"{p.Done}/{p.Total}  {Format.HumanSize(p.Bytes)}  {p.Current}";
                if (p.Phase is "ok" or "failed" && p.Current.Length > 0)
                    Log($"[{p.Done}/{p.Total}] {p.Phase,6}  {Format.HumanSize(p.Bytes),9}  {p.Current}");
            }
            catch
            {
                // 进度回调在 UI 线程执行，异常必须就地吞掉
            }
        });

        try
        {
            var result = await _downloader.DownloadAllAsync(_info, resources, _settings, uiProgress, _downloadCts.Token);
            ProgressText = $"完成：成功 {result.OkCount}/{result.TotalCount}，合计 {Format.HumanSize(result.TotalBytes)}";
            StatusText = $"已下载 {result.OkCount} 个资源 · {Format.HumanSize(result.TotalBytes)}";
            Log(ProgressText);
            Info("下载完成", $"{result.OkCount}/{result.TotalCount} 个资源已保存");
            HistoryChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已取消";
            Log("下载已取消");
        }
        catch (Exception ex)
        {
            ProgressText = $"失败：{ex.Message}";
            Log($"下载失败：{ex.Message}");
            Warn(ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
            _ = SettingsService.SaveAsync(_settings);
        }
    }

    private void OpenOutput()
    {
        var dir = OutputDir;
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        else
            Warn("输出目录尚不存在");
    }

    private void OpenArticle()
    {
        var url = _info?.FinalUrl is { Length: > 0 } f ? f : Url.Trim();
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Warn("暂无可打开的原文链接");
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    public void SetOutputDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        OutputDir = dir;
        _ = SettingsService.SaveAsync(_settings);
    }

    // ---- 工具 ----

    private void Log(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss}  {line}";
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LogLines.Add(text);
                while (LogLines.Count > 400) LogLines.RemoveAt(0);
            }
            catch
            {
                // 日志区域异常不得影响主流程
            }
        });
    }

    private void Info(string title, string content) =>
        _toast.CreateSimpleInfoToast().WithTitle(title).WithContent(content).Queue();

    private void Warn(string content) =>
        _toast.CreateToast()
            .WithTitle("提示")
            .WithContent(content)
            .OfType(NotificationType.Warning)
            .Dismiss().ByClicking()
            .Dismiss().After(TimeSpan.FromSeconds(4))
            .Queue();
}
