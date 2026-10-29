using System;
using System.Collections.Generic;

namespace WechatArticleDownloader.Models;

public enum ResourceKind
{
    Image,
    Video,
    Audio,
}

/// <summary>文章元数据。</summary>
public class ArticleInfo
{
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string Account { get; set; } = "";
    public string PublishTime { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string FetchedAt { get; set; } = "";
}

/// <summary>文章内嵌资源条目。</summary>
public class ResourceItem
{
    public int Index { get; set; }
    public ResourceKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Alt { get; set; } = "";
    public string EmbedUrl { get; set; } = "";
    public string Vid { get; set; } = "";

    public string LocalPath { get; set; } = "";
    public long Size { get; set; }
    public string Status { get; set; } = "pending";
    public string Error { get; set; } = "";
}

/// <summary>应用设置（settings.json）。</summary>
public class AppSettings
{
    public string OutputDir { get; set; } = "";
    public int Workers { get; set; } = 4;
    public int DelayMs { get; set; } = 150;
    public int Retries { get; set; } = 3;
    public bool OriginalImage { get; set; } = false;
    public bool DownloadImages { get; set; } = true;
    public bool DownloadVideos { get; set; } = true;
    public bool DownloadAudios { get; set; } = true;
    public bool DownloadCover { get; set; } = true;
    public bool SaveHtml { get; set; } = true;
}

/// <summary>下载进度报告。</summary>
public record DownloadProgress(int Done, int Total, string Current, long Bytes, string Phase);

/// <summary>下载记录条目（来自 manifest.json）。</summary>
public class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Account { get; set; } = "";
    public string FetchedAt { get; set; } = "";
    public string Directory { get; set; } = "";
    public int Images { get; set; }
    public int Videos { get; set; }
    public int Audios { get; set; }
    public long TotalBytes { get; set; }
    public bool HasReport { get; set; }

    public string TotalSizeText => Utils.Format.HumanSize(TotalBytes);
}
