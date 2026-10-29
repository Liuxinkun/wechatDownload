using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WechatArticleDownloader.Models;
using WechatArticleDownloader.Services;

namespace WechatArticleDownloader.Services;

/// <summary>
/// 命令行模式：WechatArticleDownloader.exe --cli &lt;url&gt; [--download] [--out &lt;dir&gt;]
/// 便于脚本调用与自动化验证，复用与 GUI 完全相同的服务层。
/// </summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* GUI 子系统无控制台句柄 */ }
            var url = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                      ?? throw new ArgumentException("缺少文章链接");
            var download = args.Contains("--download");
            var outIdx = Array.IndexOf(args, "--out");

            using var svc = new WechatArticleService();
            Console.WriteLine("[1/3] 正在抓取文章…");
            var (html, finalUrl) = await svc.FetchArticleAsync(url);

            Console.WriteLine("[2/3] 正在解析…");
            var meta = svc.ParseMeta(html, url, finalUrl);
            var images = svc.ParseImages(html);
            var videos = svc.ParseVideos(html);
            var audios = svc.ParseAudios(html);

            Console.WriteLine($"标题:   {meta.Title}");
            Console.WriteLine($"公众号: {meta.Account}    发布: {meta.PublishTime}");
            Console.WriteLine($"资源:   图片 {images.Count} | 视频 {videos.Count} | 音频 {audios.Count}");

            if (!download)
            {
                foreach (var r in images.Take(5))
                    Console.WriteLine($"  [image] {r.Url[..Math.Min(100, r.Url.Length)]}");
                Console.WriteLine("（--cli 模式：仅解析。加 --download 参数下载全部资源）");
                return 0;
            }

            var settings = SettingsService.Load();
            if (outIdx >= 0 && outIdx + 1 < args.Length)
                settings.OutputDir = args[outIdx + 1];

            var resources = new System.Collections.Generic.List<ResourceItem>();
            if (!string.IsNullOrEmpty(meta.CoverUrl))
                resources.Add(new ResourceItem { Kind = ResourceKind.Image, Url = meta.CoverUrl, Origin = "cover" });
            resources.AddRange(images);
            resources.AddRange(videos);
            resources.AddRange(audios);

            Console.WriteLine($"[3/3] 开始下载 {resources.Count} 个资源…");
            var downloader = new DownloadService(svc);
            var progress = new Progress<DownloadProgress>(p =>
                Console.WriteLine($"  [{p.Done}/{p.Total}] {p.Phase,-6} {Utils.Format.HumanSize(p.Bytes),9}  {p.Current}"));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await downloader.DownloadAllAsync(meta, resources, settings, progress, default);
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"完成：成功 {result.OkCount}/{result.TotalCount}，" +
                              $"合计 {Utils.Format.HumanSize(result.TotalBytes)}，耗时 {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"输出目录: {result.OutputDir}");
            return result.OkCount == result.TotalCount ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex.Message}");
            return 2;
        }
    }
}
