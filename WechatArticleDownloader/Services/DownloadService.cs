using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WechatArticleDownloader.Models;

namespace WechatArticleDownloader.Services;

public partial class DownloadService
{
    private readonly WechatArticleService _articleService;

    public DownloadService(WechatArticleService articleService)
    {
        _articleService = articleService;
    }

    public class DownloadResult
    {
        public string OutputDir { get; set; } = "";
        public int OkCount { get; set; }
        public int TotalCount { get; set; }
        public long TotalBytes { get; set; }
        public List<ResourceItem> Resources { get; set; } = new();
    }

    /// <summary>并发下载全部资源并生成 manifest.json。</summary>
    public async Task<DownloadResult> DownloadAllAsync(
        ArticleInfo meta,
        List<ResourceItem> resources,
        AppSettings settings,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        var title = SanitizeFileName(meta.Title);
        var outDir = string.IsNullOrWhiteSpace(settings.OutputDir)
            ? Path.Combine(Environment.CurrentDirectory, "downloads", title)
            : Path.Combine(settings.OutputDir, title);
        Directory.CreateDirectory(outDir);
        foreach (var sub in new[] { "images", "videos", "audio" })
            Directory.CreateDirectory(Path.Combine(outDir, sub));

        var referer = string.IsNullOrEmpty(meta.FinalUrl) ? meta.Url : meta.FinalUrl;
        var done = 0;
        var total = resources.Count;
        var okCount = 0;
        long totalBytes = 0;

        // 统一分配序号（按传入顺序：封面在前）
        for (var i = 0; i < resources.Count; i++)
        {
            resources[i].Index = i + 1;
            if (i == 0 && resources[i].Origin == "cover") resources[i].Index = 0;
        }

        using var gate = new SemaphoreSlim(Math.Max(1, settings.Workers));
        var tasks = resources.Select(r => Task.Run(async () =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await Task.Delay(settings.DelayMs, ct);
                var ext = await DownloadOneAsync(r, referer, outDir, settings, ct);
                r.LocalPath = r.Origin == "cover"
                    ? $"cover{ext}"
                    : $"{SubDirOf(r.Kind)}/{r.Index:000}_{ShortHash(r.Url)}{ext}";
                r.Size = new FileInfo(Path.Combine(outDir, r.LocalPath.Replace('/', Path.DirectorySeparatorChar))).Length;
                r.Status = "ok";
                Interlocked.Increment(ref okCount);
                Interlocked.Add(ref totalBytes, r.Size);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                r.Status = "failed";
                r.Error = ex.Message;
            }
            finally
            {
                gate.Release();
            }
            var idx = Interlocked.Increment(ref done);
            progress.Report(new DownloadProgress(idx, total,
                r.LocalPath.Length > 0 ? r.LocalPath : r.Url, r.Size, r.Status));
        }, ct)).ToList();

        await Task.WhenAll(tasks);

        // manifest.json（与 Python 版结构一致，便于互相兼容）
        WriteManifest(outDir, meta, resources);

        progress.Report(new DownloadProgress(total, total, "完成", totalBytes, "done"));
        return new DownloadResult
        {
            OutputDir = outDir,
            OkCount = okCount,
            TotalCount = total,
            TotalBytes = totalBytes,
            Resources = resources,
        };
    }

    private async Task<string> DownloadOneAsync(ResourceItem r, string referer, string outDir, AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Url))
            throw new InvalidOperationException("无可下载地址（仅记录元数据）");

        var data = await FetchWithRetriesAsync(r.Url, referer, settings.Retries, ct);

        // 原图模式：mmbiz /0 尺寸，失败回退
        if (settings.OriginalImage && r.Kind == ResourceKind.Image)
        {
            var original = OriginalVariantRegex().Replace(r.Url, "$1/0$2");
            if (!string.Equals(original, r.Url, StringComparison.Ordinal))
            {
                try
                {
                    var od = await FetchWithRetriesAsync(original, referer, settings.Retries, ct);
                    data = od;
                }
                catch { /* 回退 web 图 */ }
            }
        }

        var ext = SniffExtension(data);
        var name = r.Origin == "cover" ? $"cover{ext}" : $"{r.Index:000}_{ShortHash(r.Url)}{ext}";
        var dest = r.Origin == "cover"
            ? Path.Combine(outDir, name)
            : Path.Combine(outDir, SubDirOf(r.Kind), name);
        await File.WriteAllBytesAsync(dest, data, ct);
        return ext;
    }

    private async Task<byte[]> FetchWithRetriesAsync(string url, string referer, int retries, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, retries); attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Referer", referer);
                using var resp = await _articleService.Http.SendAsync(req, ct);
                if ((int)resp.StatusCode != 200)
                {
                    if ((int)resp.StatusCode is 403 or 404)
                        throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");
                    last = new InvalidOperationException($"HTTP {(int)resp.StatusCode}");
                    await Task.Delay(800 * attempt, ct);
                    continue;
                }
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                // 微信错误响应会返回 HTML
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.StartsWith("text/html") ||
                    (data.Length > 0 && (data[0] == (byte)'<' || AsciiStartsWith(data, "<html"))))
                    throw new InvalidOperationException("返回了 HTML 错误页");
                return data;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(800 * attempt, ct);
            }
        }
        throw last ?? new InvalidOperationException("下载失败");
    }

    private static void WriteManifest(string outDir, ArticleInfo meta, List<ResourceItem> resources)
    {
        var manifest = new
        {
            article = new
            {
                meta.Url,
                meta.FinalUrl,
                meta.Title,
                meta.Account,
                meta.PublishTime,
                meta.CoverUrl,
                meta.FetchedAt,
            },
            stats = new
            {
                image = StatOf(ResourceKind.Image),
                video = StatOf(ResourceKind.Video),
                audio = StatOf(ResourceKind.Audio),
            },
            resources = resources.Select(r => new
            {
                index = r.Index,
                type = r.Kind switch { ResourceKind.Image => "image", ResourceKind.Video => "video", _ => "audio" },
                url = r.Url,
                origin = r.Origin,
                extra = new { r.Alt, r.EmbedUrl, r.Vid },
                local_path = r.LocalPath,
                size = r.Size,
                status = r.Status,
                error = r.Error,
            }),
        };

        object StatOf(ResourceKind kind) => new        {
            total = resources.Count(x => x.Kind == kind),
            ok = resources.Count(x => x.Kind == kind && x.Status == "ok"),
            failed = resources.Count(x => x.Kind == kind && x.Status != "ok"),
            bytes = resources.Where(x => x.Kind == kind && x.Status == "ok").Sum(x => x.Size),
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), json, new UTF8Encoding(false));
    }

    // ---------- 工具 ----------

    public static string SubDirOf(ResourceKind kind) => kind switch
    {
        ResourceKind.Image => "images",
        ResourceKind.Video => "videos",
        _ => "audio",
    };

    public static string SanitizeFileName(string name, int maxLen = 80)
    {
        var cleaned = InvalidCharsRegex().Replace(name ?? "", "_").Trim(' ', '.');
        cleaned = WhitespaceRegex().Replace(cleaned, " ");
        if (cleaned.Length == 0) cleaned = "untitled";
        return cleaned.Length > maxLen ? cleaned[..maxLen] : cleaned;
    }

    public static string ShortHash(string text, int n = 8)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(n);
        for (var i = 0; i < (n + 1) / 2; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString(0, n);
    }

    public static string SniffExtension(byte[] data)
    {
        if (StartsWith(data, "GIF87a"u8) || StartsWith(data, "GIF89a"u8)) return ".gif";
        if (StartsWith(data, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (StartsWith(data, new byte[] { 0xFF, 0xD8, 0xFF })) return ".jpg";
        if (StartsWith(data, "RIFF"u8) && data.Length >= 12 && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        if (data.Length >= 12 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return ".mp4";
        if (StartsWith(data, "ID3"u8)) return ".mp3";
        if (StartsWith(data, "#!AMR"u8)) return ".amr";
        if (StartsWith(data, "OggS"u8)) return ".ogg";
        return ".bin";
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> magic) =>
        data.Length >= magic.Length && data.AsSpan(0, magic.Length).SequenceEqual(magic);

    private static bool AsciiStartsWith(byte[] data, string s)
    {
        if (data.Length < s.Length) return false;
        for (var i = 0; i < s.Length; i++)
            if (data[i] != (byte)s[i]) return false;
        return true;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidCharsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(mmbiz\.qpic\.cn/[^/]+/[^/]+)/\d+(\?)")]
    private static partial Regex OriginalVariantRegex();
}
