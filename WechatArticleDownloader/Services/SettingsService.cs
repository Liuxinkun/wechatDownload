using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WechatArticleDownloader.Models;

namespace WechatArticleDownloader.Services;

/// <summary>settings.json 持久化（存放于程序目录）。</summary>
public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
        if (string.IsNullOrWhiteSpace(Current.OutputDir))
            Current.OutputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "公众号文章下载");
        return Current;
    }

    public static async Task SaveAsync(AppSettings settings)
    {
        Current = settings;
        var json = JsonSerializer.Serialize(settings, Options);
        await File.WriteAllTextAsync(SettingsPath, json);
    }
}

/// <summary>扫描输出目录下的 manifest.json 生成下载记录。</summary>
public static class HistoryService
{
    public static List<HistoryEntry> Load(string outputDir)
    {
        var list = new List<HistoryEntry>();
        if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir)) return list;
        foreach (var manifestPath in Directory.EnumerateFiles(outputDir, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;
                var article = root.GetProperty("article");
                var stats = root.GetProperty("stats");

                int Stat(string kind, string field)
                {
                    if (!stats.TryGetProperty(kind, out var s)) return 0;
                    return s.TryGetProperty(field, out var v) && v.TryGetInt32(out var i) ? i : 0;
                }

                long Bytes(string kind)
                {
                    if (!stats.TryGetProperty(kind, out var s)) return 0;
                    return s.TryGetProperty("bytes", out var v) && v.TryGetInt64(out var l) ? l : 0;
                }

                list.Add(new HistoryEntry
                {
                    Title = article.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Account = article.TryGetProperty("account", out var a) ? a.GetString() ?? "" : "",
                    FetchedAt = article.TryGetProperty("fetched_at", out var f) ? f.GetString() ?? "" : "",
                    Directory = Path.GetDirectoryName(manifestPath) ?? "",
                    Images = Stat("image", "ok"),
                    Videos = Stat("video", "ok"),
                    Audios = Stat("audio", "ok"),
                    TotalBytes = Bytes("image") + Bytes("video") + Bytes("audio"),
                    HasReport = File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, "index.html")),
                });
            }
            catch
            {
                // 跳过损坏的 manifest
            }
        }
        return list.OrderByDescending(x => x.FetchedAt).ToList();
    }
}
