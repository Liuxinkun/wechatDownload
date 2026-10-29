using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using WechatArticleDownloader.Models;

namespace WechatArticleDownloader.Services;

/// <summary>
/// 微信公众号文章抓取与解析服务。
/// 策略：多微信 UA 候选链 + 自动重定向 + Cookie 会话，绕过 MMLAS 风控验证页；
/// 图片下载需携带 Referer: https://mp.weixin.qq.com/。
/// </summary>
public partial class WechatArticleService : IDisposable
{
    private static readonly string[] UaCandidates =
    {
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0.49(0x18003128) NetType/WIFI Language/zh_CN",
        "Mozilla/5.0 (Linux; Android 13; PJD110 Build/TP1A.220905.001; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/114.0.5735.196 Mobile Safari/537.36 XWEB/1160053 MMWEBSDK/20231204 MicroMessenger/8.0.47.2560(0x28002F51) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
    };

    private static readonly string[] VerifyPageMarkers =
    {
        "未知错误", "失效的验证页面", "你暂无权限查看此页面内容", "环境异常",
    };

    private readonly HttpClient _http;

    public WechatArticleService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    }

    // ---------- 抓取 ----------

    public async Task<(string html, string finalUrl)> FetchArticleAsync(string url, CancellationToken ct = default)
    {
        var lastError = "";
        foreach (var ua in UaCandidates)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", ua);
                    using var resp = await _http.SendAsync(req, ct);
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    if ((int)resp.StatusCode == 200 && !IsVerificationPage(text))
                        return (text, resp.RequestMessage?.RequestUri?.ToString() ?? url);
                    lastError = $"HTTP {(int)resp.StatusCode} 或风控验证页";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex.Message;
                }
                await Task.Delay(600 * attempt, ct);
            }
        }
        throw new InvalidOperationException($"文章抓取失败：{lastError}。可能原因：链接失效、IP 被微信风控或网络代理异常。");
    }

    private static bool IsVerificationPage(string html)
    {
        if (string.IsNullOrEmpty(html)) return true;
        var head = html.Length > 4000 ? html[..4000] : html;
        if (VerifyPageMarkers.Any(head.Contains)) return true;
        return !html.Contains("js_content");
    }

    // ---------- 解析 ----------

    public ArticleInfo ParseMeta(string html, string url, string finalUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var info = new ArticleInfo { Url = url, FinalUrl = finalUrl };

        info.Title = MetaContent(doc, "og:title");
        if (string.IsNullOrWhiteSpace(info.Title))
        {
            var h1 = doc.DocumentNode.SelectSingleNode("//*[@id='activity-name'] | //*[contains(@class,'rich_media_title')]");
            info.Title = h1?.InnerText.Trim() ?? "";
        }
        if (string.IsNullOrWhiteSpace(info.Title))
        {
            var m = MsgTitleRegex().Match(html);
            if (m.Success) info.Title = WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        info.Title = info.Title.Trim();
        if (info.Title.Length == 0) info.Title = "未命名文章";

        info.Account = MetaContent(doc, "og:article:author");
        if (string.IsNullOrWhiteSpace(info.Account))
        {
            var name = doc.DocumentNode.SelectSingleNode("//*[@id='js_name']");
            info.Account = name?.InnerText.Trim() ?? "";
        }

        var ct = CtRegex().Match(html);
        if (ct.Success && long.TryParse(ct.Groups[1].Value, out var ts))
        {
            var dto = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
            info.PublishTime = dto.ToString("yyyy-MM-dd HH:mm:ss");
        }

        var cover = MsgCdnUrlRegex().Match(html);
        info.CoverUrl = cover.Success ? cover.Groups[1].Value : MetaContent(doc, "og:image");
        info.FetchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return info;
    }

    public List<ResourceItem> ParseImages(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var content = doc.DocumentNode.SelectSingleNode("//*[@id='js_content']") ?? doc.DocumentNode;
        var list = new List<ResourceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? raw, string alt, string origin)
        {
            var u = NormalizeUrl(raw);
            if (u is null || !u.Contains("mmbiz.qpic.cn")) return;
            var key = u.Split('?')[0];
            if (!seen.Add(key)) return;
            list.Add(new ResourceItem { Kind = ResourceKind.Image, Url = u, Alt = alt?.Trim() ?? "", Origin = origin });
        }

        foreach (var img in content.SelectNodes(".//img") ?? Enumerable.Empty<HtmlNode>())
        {
            var src = img.GetAttributeValue("data-src", "") is { Length: > 0 } ds ? ds : img.GetAttributeValue("src", "");
            Add(src, img.GetAttributeValue("alt", ""), "data-src");
        }
        // 背景图兜底
        var contentHtml = content.InnerHtml;
        foreach (Match m in BgImageRegex().Matches(contentHtml))
            Add(m.Groups[1].Value, "", "background");
        return list;
    }

    public List<ResourceItem> ParseVideos(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var content = doc.DocumentNode.SelectSingleNode("//*[@id='js_content']") ?? doc.DocumentNode;
        var list = new List<ResourceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(ResourceItem item)
        {
            var key = item.Url is { Length: > 0 } ? item.Url : item.EmbedUrl;
            if (key.Length == 0 || !seen.Add(key)) return;
            list.Add(item);
        }

        foreach (var iframe in content.SelectNodes(".//iframe") ?? Enumerable.Empty<HtmlNode>())
        {
            var src = NormalizeUrl(iframe.GetAttributeValue("data-src", "") is { Length: > 0 } ds ? ds : iframe.GetAttributeValue("src", ""));
            if (src is null) continue;
            if (src.Contains("v.qq.com"))
            {
                var m = VidRegex().Match(src);
                Add(new ResourceItem
                {
                    Kind = ResourceKind.Video,
                    Origin = "qq_video_iframe",
                    EmbedUrl = src,
                    Vid = m.Success ? m.Groups[1].Value : "",
                    Url = m.Success ? $"https://vv.video.qq.com/getinfo?vids={m.Groups[1].Value}" : "",
                });
            }
            else if (src.Contains("videoplayer") && src.Contains("get_mp_video_play_url"))
            {
                Add(new ResourceItem { Kind = ResourceKind.Video, Origin = "wechat_native_iframe", EmbedUrl = src, Url = src });
            }
            else if (src.Contains("channels.weixin.qq.com"))
            {
                Add(new ResourceItem { Kind = ResourceKind.Video, Origin = "wechat_channels", EmbedUrl = src, Url = src });
            }
        }

        foreach (var tag in content.SelectNodes(".//mpvideo | .//mpvideosnap | .//mp-common-videosnap") ?? Enumerable.Empty<HtmlNode>())
        {
            var src = NormalizeUrl(tag.GetAttributeValue("data-src", "") is { Length: > 0 } ds ? ds : tag.GetAttributeValue("src", ""));
            var vid = tag.GetAttributeValue("vid", "");
            if (string.IsNullOrEmpty(vid)) vid = tag.GetAttributeValue("object_id", "");
            Add(new ResourceItem
            {
                Kind = ResourceKind.Video,
                Origin = $"wechat_{tag.Name}",
                EmbedUrl = src ?? "",
                Vid = vid,
                Url = vid.Length > 0 ? $"https://mp.weixin.qq.com/mp/videoplayer?action=get_mp_video_play_url&vid={vid}" : src ?? "",
            });
        }

        foreach (Match m in ScriptVidRegex().Matches(html))
        {
            Add(new ResourceItem { Kind = ResourceKind.Video, Origin = "qq_video_script", Vid = m.Groups[1].Value });
        }
        return list;
    }

    public List<ResourceItem> ParseAudios(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var content = doc.DocumentNode.SelectSingleNode("//*[@id='js_content']") ?? doc.DocumentNode;
        var list = new List<ResourceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in content.SelectNodes(".//mpvoice") ?? Enumerable.Empty<HtmlNode>())
        {
            var src = NormalizeUrl(tag.GetAttributeValue("src", ""));
            if (src is null || !seen.Add(src)) continue;
            list.Add(new ResourceItem { Kind = ResourceKind.Audio, Url = src, Origin = "mpvoice", Alt = tag.GetAttributeValue("name", "") });
        }
        foreach (Match m in VoiceRegex().Matches(html))
        {
            var u = m.Groups[1].Value;
            if (seen.Add(u))
                list.Add(new ResourceItem { Kind = ResourceKind.Audio, Url = u, Origin = "voice_regex" });
        }
        return list;
    }

    /// <summary>下载封面缩略图字节（供 UI 预览）。</summary>
    public async Task<byte[]?> DownloadCoverAsync(string coverUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, coverUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", UaCandidates[0]);
            req.Headers.TryAddWithoutValidation("Referer", "https://mp.weixin.qq.com/");
            using var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode != 200) return null;
            var data = await resp.Content.ReadAsByteArrayAsync(ct);
            return data.Length > 0 && data[0] != '<' ? data : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>共享 HttpClient 供下载器复用（带 Referer 由下载请求单独指定）。</summary>
    public HttpClient Http => _http;

    public void Dispose() => _http.Dispose();

    // ---------- 工具 ----------

    public static string? NormalizeUrl(string? raw)
    {
        var u = WebUtility.HtmlDecode(raw?.Trim() ?? "");
        if (u.StartsWith("//")) u = "https:" + u;
        return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : null;
    }

    private static string MetaContent(HtmlDocument doc, string property)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@property='{property}']");
        var v = node?.GetAttributeValue("content", "") ?? "";
        return WebUtility.HtmlDecode(v).Trim();
    }

    [GeneratedRegex(@"var\s+msg_title\s*=\s*""([^""]*)""")]
    private static partial Regex MsgTitleRegex();

    [GeneratedRegex(@"var\s+ct\s*=\s*""(\d{9,11})""")]
    private static partial Regex CtRegex();

    [GeneratedRegex(@"var\s+msg_cdn_url\s*=\s*""([^""]+)""")]
    private static partial Regex MsgCdnUrlRegex();

    [GeneratedRegex(@"url\(\s*[""']?(https?://mmbiz\.qpic\.cn/[^)""'\s]+)")]
    private static partial Regex BgImageRegex();

    [GeneratedRegex(@"[?&]vid=([a-zA-Z0-9]+)")]
    private static partial Regex VidRegex();

    [GeneratedRegex(@"v\.qq\.com/(?:iframe/preview|txp)[^""'\s]*?vid=([a-zA-Z0-9]+)")]
    private static partial Regex ScriptVidRegex();

    [GeneratedRegex(@"(https?://res\.wx\.qq\.com/voice/getvoice\?mediaid=[^""'\s&]+)")]
    private static partial Regex VoiceRegex();
}
