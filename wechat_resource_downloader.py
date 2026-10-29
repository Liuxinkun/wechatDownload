#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
wechat_resource_downloader.py — 微信公众号文章资源爬取下载工具

功能：
  1. 抓取微信公众号文章页面（自动处理短链跳转与风控验证页，内置微信 UA 策略）；
  2. 解析文章元数据（标题、公众号名称、发布时间、封面图）；
  3. 提取并下载文章内嵌资源：
       - 图片（data-src 懒加载、背景图、封面图，支持原图模式）
       - 视频（腾讯视频 iframe 解析 vid、公众号原生 mpvideo、视频号链接记录）
       - 音频（mpvoice）
  4. 生成 manifest.json 资源清单与 index.html 可视化报告。

用法：
  python wechat_resource_downloader.py <文章URL> [选项]

示例：
  python wechat_resource_downloader.py https://mp.weixin.qq.com/s/xxxxxx
  python wechat_resource_downloader.py https://mp.weixin.qq.com/s/xxxxxx -o out --original --save-html
  python wechat_resource_downloader.py https://mp.weixin.qq.com/s/xxxxxx --types images --limit 10 --list-only

依赖：
  pip install requests beautifulsoup4 lxml
"""

from __future__ import annotations

import argparse
import hashlib
import html as html_mod
import json
import logging
import os
import re
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Iterable, Optional

try:
    import requests
    from bs4 import BeautifulSoup
except ImportError:  # pragma: no cover
    sys.stderr.write("缺少依赖，请先执行: pip install requests beautifulsoup4 lxml\n")
    raise

# ---------------------------------------------------------------------------
# 常量
# ---------------------------------------------------------------------------

UA_WECHAT_IOS = (
    "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) "
    "AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 "
    "MicroMessenger/8.0.49(0x18003128) NetType/WIFI Language/zh_CN"
)
UA_WECHAT_ANDROID = (
    "Mozilla/5.0 (Linux; Android 13; PJD110 Build/TP1A.220905.001; wv) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/114.0.5735.196 "
    "Mobile Safari/537.36 XWEB/1160053 MMWEBSDK/20231204 MMWEBID/8517 "
    "MicroMessenger/8.0.47.2560(0x28002F51) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64"
)
UA_PC_CHROME = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
)
UA_CANDIDATES = [UA_WECHAT_IOS, UA_WECHAT_ANDROID, UA_PC_CHROME]

VERIFY_PAGE_MARKERS = (
    "未知错误",
    "失效的验证页面",
    "你暂无权限查看此页面内容",
    "环境异常",
)

ILLEGAL_FILENAME_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
MMBIZ_FMT_SEGMENT = re.compile(r"(mmbiz\.qpic\.cn/[^/]+/[^/]+)/\d+(\?)")

CONTENT_TYPE_EXT = {
    "image/gif": ".gif",
    "image/jpeg": ".jpg",
    "image/jpg": ".jpg",
    "image/png": ".png",
    "image/webp": ".webp",
    "image/bmp": ".bmp",
    "image/x-icon": ".ico",
    "video/mp4": ".mp4",
    "video/quicktime": ".mov",
    "video/webm": ".webm",
    "audio/mpeg": ".mp3",
    "audio/mp3": ".mp3",
    "audio/mp4": ".m4a",
    "audio/amr": ".amr",
    "audio/x-m4a": ".m4a",
}

MAGIC_EXT = [
    (b"GIF87a", ".gif"),
    (b"GIF89a", ".gif"),
    (b"\x89PNG\r\n\x1a\n", ".png"),
    (b"\xff\xd8\xff", ".jpg"),
    (b"RIFF", ".webp"),  # 需结合 WEBP 标记
    (b"ID3", ".mp3"),
    (b"#!AMR", ".amr"),
    (b"\x1aE\xdf\xa3", ".mkv"),
]

log = logging.getLogger("wechat-resource-downloader")


# ---------------------------------------------------------------------------
# 数据结构
# ---------------------------------------------------------------------------

@dataclass
class ArticleMeta:
    url: str
    title: str = ""
    account: str = ""
    publish_time: str = ""
    cover_url: str = ""
    fetched_at: str = ""
    final_url: str = ""


@dataclass
class Resource:
    index: int
    rtype: str  # image / video / audio
    url: str
    origin: str = ""          # 页面中的来源标记（如 data-src / iframe / mpvoice）
    extra: dict = field(default_factory=dict)
    local_path: str = ""
    size: int = 0
    status: str = "pending"   # pending / ok / failed / skipped / metadata_only
    error: str = ""


# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------

def sanitize_filename(name: str, maxlen: int = 80) -> str:
    """清理为合法且可读的文件名。"""
    name = ILLEGAL_FILENAME_CHARS.sub("_", name or "").strip(" .")
    name = re.sub(r"\s+", " ", name)
    return (name or "untitled")[:maxlen]


def short_hash(text: str, n: int = 8) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()[:n]


def sniff_extension(head: bytes, content_type: str, url: str) -> str:
    """根据 magic bytes / Content-Type / URL 后缀推断扩展名。"""
    for magic, ext in MAGIC_EXT:
        if head.startswith(magic):
            if ext == ".webp" and b"WEBP" not in head[:16]:
                continue
            return ext
    if b"ftyp" in head[:16]:
        return ".mp4"
    ct = (content_type or "").split(";")[0].strip().lower()
    if ct in CONTENT_TYPE_EXT:
        return CONTENT_TYPE_EXT[ct]
    m = re.search(r"\.(gif|jpe?g|png|webp|bmp|mp4|mov|webm|mp3|m4a|amr)(?:$|[?#])", url, re.I)
    if m:
        e = m.group(1).lower()
        return {".jpeg": ".jpg"}.get(e, e) if e.startswith(".") else "." + e
    return ".bin"


def human_size(n: int) -> str:
    if n < 1024:
        return f"{n} B"
    if n < 1024 ** 2:
        return f"{n / 1024:.1f} KB"
    if n < 1024 ** 3:
        return f"{n / 1024 ** 2:.1f} MB"
    return f"{n / 1024 ** 3:.2f} GB"


def normalize_media_url(url: str) -> str:
    """协议相对 URL 补全 https，并做 HTML 实体反转义。"""
    url = html_mod.unescape((url or "").strip())
    if url.startswith("//"):
        url = "https:" + url
    return url


# ---------------------------------------------------------------------------
# 文章抓取
# ---------------------------------------------------------------------------

def is_verification_page(html_text: str) -> bool:
    if not html_text:
        return True
    head = html_text[:4000]
    return any(marker in head for marker in VERIFY_PAGE_MARKERS) or "js_content" not in html_text


def fetch_article(url: str, timeout: int = 30, retries: int = 2) -> tuple[str, str]:
    """抓取文章页面，返回 (html, final_url)。按 UA 候选依次尝试。"""
    last_err = ""
    for ua in UA_CANDIDATES:
        session = requests.Session()
        session.headers.update({
            "User-Agent": ua,
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "Accept-Language": "zh-CN,zh;q=0.9",
        })
        for attempt in range(1, retries + 1):
            try:
                resp = session.get(url, timeout=timeout, allow_redirects=True)
                text = resp.text
                if resp.status_code == 200 and not is_verification_page(text):
                    log.info("文章抓取成功（UA: %s…，%d 字节）", ua[:24], len(text))
                    return text, resp.url
                last_err = f"HTTP {resp.status_code} / 疑似风控验证页"
            except requests.RequestException as exc:
                last_err = str(exc)
            time.sleep(0.8 * attempt)
        log.warning("UA 候选失败：%s（%s）", last_err, ua[:32])
    raise RuntimeError(f"文章抓取失败：{last_err}。可能原因：链接失效、IP 被微信风控、或网络代理异常。")


# ---------------------------------------------------------------------------
# 文章解析
# ---------------------------------------------------------------------------

class ArticleParser:
    def __init__(self, html_text: str, url: str, final_url: str):
        self.html = html_text
        self.url = url
        self.final_url = final_url
        self.soup = BeautifulSoup(html_text, "lxml")
        self.content = self.soup.select_one("#js_content") or self.soup.body or self.soup

    # ---- 元数据 ----
    def parse_meta(self) -> ArticleMeta:
        soup = self.soup
        title = ""
        og_title = soup.find("meta", attrs={"property": "og:title"})
        if og_title and og_title.get("content"):
            title = og_title["content"].strip()
        if not title:
            h1 = soup.select_one("#activity-name, .rich_media_title")
            if h1:
                title = h1.get_text(strip=True)
        if not title:
            m = re.search(r'var\s+msg_title\s*=\s*"([^"]*)"', self.html)
            title = html_mod.unescape(m.group(1)) if m else "未命名文章"

        account = ""
        og_author = soup.find("meta", attrs={"property": "og:article:author"})
        if og_author and og_author.get("content"):
            account = og_author["content"].strip()
        if not account:
            js_name = soup.select_one("#js_name")
            if js_name:
                account = js_name.get_text(strip=True)
        if not account:
            m = re.search(r'var\s+nickname\s*=\s*"([^"]*)"', self.html)
            account = html_mod.unescape(m.group(1)) if m else ""

        publish = ""
        m = re.search(r'var\s+ct\s*=\s*"(\d{9,11})"', self.html)
        if m:
            publish = datetime.fromtimestamp(int(m.group(1))).strftime("%Y-%m-%d %H:%M:%S")
        if not publish:
            m = re.search(r'var\s+create_time\s*=\s*"([^"]*)"', self.html)
            publish = m.group(1) if m else ""

        cover = ""
        m = re.search(r'var\s+msg_cdn_url\s*=\s*"([^"]+)"', self.html)
        if m:
            cover = normalize_media_url(m.group(1))
        if not cover:
            og_img = soup.find("meta", attrs={"property": "og:image"})
            if og_img and og_img.get("content"):
                cover = normalize_media_url(og_img["content"])

        return ArticleMeta(
            url=self.url,
            title=title,
            account=account,
            publish_time=publish,
            cover_url=cover,
            fetched_at=datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            final_url=self.final_url,
        )

    # ---- 图片 ----
    def parse_images(self) -> list[dict]:
        """按页面顺序提取正文图片，返回 [{url, alt}]，已去重。"""
        found: list[dict] = []
        seen: set[str] = set()

        def add(raw_url: str, alt: str = "") -> None:
            u = normalize_media_url(raw_url)
            if not u.startswith("http") or "mmbiz.qpic.cn" not in u:
                return
            key = u.split("?")[0]
            if key in seen:
                return
            seen.add(key)
            found.append({"url": u, "alt": (alt or "").strip()})

        for img in self.content.find_all("img"):
            add(img.get("data-src") or img.get("src") or "", img.get("alt") or "")
        # 背景图 / data-* 中的图片兜底扫描
        for m in re.finditer(r'url\(\s*["\']?(https?://mmbiz\.qpic\.cn/[^)"\'\s]+)', str(self.content)):
            add(m.group(1))
        return found

    # ---- 视频 ----
    def parse_videos(self) -> list[dict]:
        videos: list[dict] = []
        seen: set[str] = set()

        def add(entry: dict) -> None:
            key = entry.get("play_url") or entry.get("embed_url") or ""
            if key and key not in seen:
                seen.add(key)
                videos.append(entry)

        for iframe in self.content.find_all("iframe"):
            src = normalize_media_url(iframe.get("data-src") or iframe.get("src") or "")
            if not src.startswith("http"):
                continue
            if "v.qq.com" in src:
                m = re.search(r"[?&]vid=([a-zA-Z0-9]+)", src)
                add({
                    "source": "qq_video_iframe",
                    "embed_url": src,
                    "vid": m.group(1) if m else "",
                    "play_url": f"https://vv.video.qq.com/getinfo?vids={m.group(1)}" if m else "",
                })
            elif "videoplayer" in src and "get_mp_video_play_url" in src:
                add({"source": "wechat_native_iframe", "embed_url": src, "play_url": src})
            elif "channels.weixin.qq.com" in src:
                add({"source": "wechat_channels", "embed_url": src})
        # 原生 mpvideo / mpvideosnap 标签
        for tag in self.content.find_all(["mpvideo", "mpvideosnap", "mp-common-videosnap"]):
            attrs = {k: (v if isinstance(v, str) else " ".join(v)) for k, v in tag.attrs.items()}
            src = normalize_media_url(attrs.get("data-src") or attrs.get("src") or "")
            vid = attrs.get("vid") or attrs.get("object_id") or ""
            add({
                "source": f"wechat_{tag.name}",
                "embed_url": src,
                "vid": vid,
                "play_url": f"https://mp.weixin.qq.com/mp/videoplayer?action=get_mp_video_play_url&vid={vid}" if vid else "",
            })
        # 全文兜底：页面脚本中的腾讯视频 vid
        for m in re.finditer(r'v\.qq\.com/(?:iframe/preview|txp)[^"\'\s]*?vid=([a-zA-Z0-9]+)', self.html):
            add({"source": "qq_video_script", "embed_url": "", "vid": m.group(1)})
        return videos

    # ---- 音频 ----
    def parse_audios(self) -> list[dict]:
        audios: list[dict] = []
        seen: set[str] = set()
        for tag in self.content.find_all("mpvoice"):
            src = normalize_media_url(tag.get("src") or "")
            name = tag.get("name") or ""
            if src.startswith("http") and src not in seen:
                seen.add(src)
                audios.append({"url": src, "name": name, "source": "mpvoice"})
        for m in re.finditer(r'(https?://res\.wx\.qq\.com/voice/getvoice\?mediaid=[^"\'\s&]+)', self.html):
            u = m.group(1)
            if u not in seen:
                seen.add(u)
                audios.append({"url": u, "name": "", "source": "voice_regex"})
        return audios


# ---------------------------------------------------------------------------
# 资源下载
# ---------------------------------------------------------------------------

class ResourceDownloader:
    def __init__(self, session: requests.Session, referer: str, timeout: int = 30, retries: int = 3):
        self.session = session
        self.referer = referer
        self.timeout = timeout
        self.retries = retries

    def fetch_bytes(self, url: str) -> tuple[bytes, str]:
        """下载资源字节，返回 (content, content_type)。"""
        headers = {"Referer": self.referer}
        last = ""
        for attempt in range(1, self.retries + 1):
            try:
                with self.session.get(url, headers=headers, timeout=(10, self.timeout), stream=True) as r:
                    if r.status_code != 200:
                        last = f"HTTP {r.status_code}"
                        if r.status_code in (403, 404):
                            break
                        time.sleep(1.0 * attempt)
                        continue
                    chunks = []
                    for chunk in r.iter_content(chunk_size=256 * 1024):
                        if chunk:
                            chunks.append(chunk)
                    data = b"".join(chunks)
                    ctype = r.headers.get("Content-Type", "")
                    if ctype.startswith("text/html") or data[:200].find(b"<html") >= 0:
                        last = "返回了 HTML 错误页"
                        break
                    return data, ctype
            except requests.RequestException as exc:
                last = str(exc)
                time.sleep(1.0 * attempt)
        raise RuntimeError(last or "下载失败")

    def resolve_qq_video(self, vid: str) -> Optional[str]:
        """尽力解析腾讯视频真实播放地址（旧版 getinfo 接口，可能失效）。"""
        api = (
            "https://vv.video.qq.com/getinfo?otype=json&platform=11001"
            f"&vids={vid}&defn=shd&charge=0&sdtfrom=v1010&appVer=3.5.57&defaultfmt=auto"
        )
        try:
            with self.session.get(api, headers={"Referer": "https://v.qq.com/"}, timeout=(10, 20)) as r:
                text = r.text
            vi = re.search(r'"vi"\s*:\s*\[\s*\{(.*?)\}', text, re.S)
            if not vi:
                return None
            block = vi.group(1)
            url_m = re.search(r'"url"\s*:\s*"([^"]+)"', block)
            fn_m = re.search(r'"fn"\s*:\s*"([^"]+)"', block)
            key_m = re.search(r'"fvkey"\s*:\s*"([^"]+)"', block)
            if url_m and fn_m and key_m:
                base = url_m.group(1).replace("http://", "https://")
                return f"{base}{fn_m.group(1)}?vkey={key_m.group(1)}"
        except requests.RequestException:
            return None
        return None


# ---------------------------------------------------------------------------
# 报告生成
# ---------------------------------------------------------------------------

def build_report(meta: ArticleMeta, resources: list[Resource], out_dir: Path, report_path: Path) -> None:
    ok = [r for r in resources if r.status == "ok"]
    failed = [r for r in resources if r.status in ("failed", "metadata_only", "skipped")]
    stats: dict[str, dict] = {}
    for r in resources:
        s = stats.setdefault(r.rtype, {"total": 0, "ok": 0, "bytes": 0})
        s["total"] += 1
        if r.status == "ok":
            s["ok"] += 1
            s["bytes"] += r.size

    def esc(s) -> str:
        return html_mod.escape(str(s), quote=True)

    rows = []
    for rtype, label in (("image", "图片"), ("video", "视频"), ("audio", "音频")):
        s = stats.get(rtype)
        if not s:
            continue
        rows.append(
            f"<tr><td>{label}</td><td>{s['total']}</td><td>{s['ok']}</td>"
            f"<td>{s['total'] - s['ok']}</td><td>{human_size(s['bytes'])}</td></tr>"
        )

    media_cards = []
    for r in ok:
        if r.rtype == "image":
            media_cards.append(
                f'<a class="cell" href="{esc(r.local_path)}" target="_blank" title="{esc(r.url)}">'
                f'<img loading="lazy" src="{esc(r.local_path)}" alt=""></a>'
            )
        elif r.rtype == "video":
            extra = esc(r.extra.get("resolved_url") or r.extra.get("embed_url") or r.url)
            media_cards.append(
                f'<div class="cell vid"><div class="vtag">视频</div>'
                f'<div class="vmeta">{esc(r.extra.get("source", ""))}</div>'
                f'<a class="vlink" href="{extra}" target="_blank">{extra}</a>'
                f'<div class="vfile">{esc(r.local_path)}</div></div>'
            )
        else:
            media_cards.append(
                f'<div class="cell aud"><div class="vtag">音频</div>'
                f'<audio controls preload="none" src="{esc(r.local_path)}"></audio>'
                f'<div class="vfile">{esc(r.local_path)}</div></div>'
            )

    fail_rows = "".join(
        f"<tr><td>{esc(r.rtype)}</td><td class=\"mono\">{esc(r.url[:110])}</td><td>{esc(r.error or r.status)}</td></tr>"
        for r in failed
    ) or '<tr><td colspan="3" class="empty">全部资源下载成功</td></tr>'

    doc = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>资源报告 - {esc(meta.title)}</title>
<style>
  :root {{ --line:#e5e7eb; --muted:#6b7280; --accent:#2563eb; --bg:#f7f8fa; --card:#ffffff; }}
  * {{ box-sizing:border-box; }}
  body {{ margin:0; font-family:-apple-system,"PingFang SC","Microsoft YaHei",sans-serif; background:var(--bg); color:#111827; }}
  .wrap {{ max-width:1080px; margin:0 auto; padding:28px 20px 60px; }}
  header {{ background:var(--card); border:1px solid var(--line); border-radius:14px; padding:22px 24px; }}
  h1 {{ font-size:20px; margin:0 0 10px; }}
  .meta {{ display:flex; flex-wrap:wrap; gap:8px 22px; color:var(--muted); font-size:13px; }}
  .meta b {{ color:#374151; font-weight:600; }}
  .cards {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:12px; margin-top:16px; }}
  .stat {{ background:var(--card); border:1px solid var(--line); border-radius:12px; padding:14px 16px; }}
  .stat .num {{ font-size:22px; font-weight:700; }}
  .stat .lbl {{ font-size:12px; color:var(--muted); margin-top:2px; }}
  table {{ width:100%; border-collapse:collapse; background:var(--card); border:1px solid var(--line); border-radius:12px; overflow:hidden; margin-top:16px; font-size:13px; }}
  th, td {{ padding:9px 14px; border-bottom:1px solid var(--line); text-align:left; }}
  th {{ background:#f3f4f6; font-weight:600; }}
  .grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:10px; margin-top:16px; }}
  .cell {{ position:relative; background:var(--card); border:1px solid var(--line); border-radius:10px; overflow:hidden; aspect-ratio:1/1; display:flex; align-items:center; justify-content:center; text-decoration:none; color:inherit; }}
  .cell img {{ width:100%; height:100%; object-fit:cover; display:block; }}
  .cell.vid, .cell.aud {{ aspect-ratio:auto; flex-direction:column; align-items:flex-start; padding:10px; gap:6px; min-height:110px; }}
  .vtag {{ background:var(--accent); color:#fff; font-size:11px; padding:2px 8px; border-radius:20px; }}
  .vmeta {{ font-size:12px; color:var(--muted); }}
  .vlink {{ font-size:12px; color:var(--accent); word-break:break-all; }}
  .vfile {{ font-size:11px; color:var(--muted); word-break:break-all; }}
  audio {{ width:100%; }}
  .mono {{ font-family:ui-monospace,Consolas,monospace; font-size:12px; word-break:break-all; }}
  .empty {{ color:var(--muted); text-align:center; }}
  h2 {{ font-size:15px; margin:26px 0 4px; }}
  .hint {{ font-size:12px; color:var(--muted); }}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>{esc(meta.title)}</h1>
    <div class="meta">
      <span>公众号：<b>{esc(meta.account or "未知")}</b></span>
      <span>发布：<b>{esc(meta.publish_time or "未知")}</b></span>
      <span>来源：<b class="mono">{esc(meta.url)}</b></span>
      <span>抓取时间：<b>{esc(meta.fetched_at)}</b></span>
    </div>
    <div class="cards">
      <div class="stat"><div class="num">{stats.get("image", {}).get("total", 0)}</div><div class="lbl">图片</div></div>
      <div class="stat"><div class="num">{stats.get("video", {}).get("total", 0)}</div><div class="lbl">视频</div></div>
      <div class="stat"><div class="num">{stats.get("audio", {}).get("total", 0)}</div><div class="lbl">音频</div></div>
      <div class="stat"><div class="num">{len(ok)}</div><div class="lbl">下载成功</div></div>
      <div class="stat"><div class="num">{human_size(sum(s["bytes"] for s in stats.values()))}</div><div class="lbl">总大小</div></div>
    </div>
  </header>
  <h2>资源统计</h2>
  <table><thead><tr><th>类型</th><th>发现</th><th>成功</th><th>失败</th><th>大小</th></tr></thead>
  <tbody>{"".join(rows) or '<tr><td colspan="5" class="empty">未发现资源</td></tr>'}</tbody></table>
  <h2>资源预览</h2>
  <div class="hint">点击图片可查看原文件；视频/音频为本地文件或可访问链接。</div>
  <div class="grid">{"".join(media_cards) or '<div class="empty">无可预览资源</div>'}</div>
  <h2>失败 / 待处理项</h2>
  <table><thead><tr><th>类型</th><th>原始地址</th><th>原因</th></tr></thead><tbody>{fail_rows}</tbody></table>
</div>
</body>
</html>
"""
    report_path.write_text(doc, encoding="utf-8")


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def build_session(ua: str) -> requests.Session:
    s = requests.Session()
    s.headers.update({
        "User-Agent": ua,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "zh-CN,zh;q=0.9",
    })
    return s


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="微信公众号文章资源爬取下载工具（图片 / 视频 / 音频 / 封面）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="示例：python wechat_resource_downloader.py https://mp.weixin.qq.com/s/xxxxxx -o out",
    )
    parser.add_argument("url", help="微信公众号文章链接（支持 /s/ 短链）")
    parser.add_argument("-o", "--output", default=None, help="输出目录（默认 downloads/<文章标题>）")
    parser.add_argument("--types", default="images,videos,audio,cover",
                        help="要下载的资源类型，逗号分隔：images,videos,audio,cover（默认全部）")
    parser.add_argument("--workers", type=int, default=4, help="并发下载线程数（默认 4）")
    parser.add_argument("--delay", type=float, default=0.15, help="每个资源下载前的间隔秒数（默认 0.15）")
    parser.add_argument("--retries", type=int, default=3, help="单个资源重试次数（默认 3）")
    parser.add_argument("--timeout", type=int, default=30, help="网络超时秒数（默认 30）")
    parser.add_argument("--limit", type=int, default=0, help="最多下载的图片数量，0 为不限（默认 0）")
    parser.add_argument("--original", action="store_true", help="尝试下载原图（mmbiz /0 尺寸，失败自动回退）")
    parser.add_argument("--save-html", action="store_true", help="同时保存文章原始 HTML")
    parser.add_argument("--no-report", action="store_true", help="不生成 index.html 报告")
    parser.add_argument("--list-only", action="store_true", help="仅解析并列出资源，不下载")
    parser.add_argument("--ua", default=UA_WECHAT_IOS, help="自定义 User-Agent")
    parser.add_argument("-v", "--verbose", action="store_true", help="详细日志")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s", datefmt="%H:%M:%S")

    want = {t.strip() for t in args.types.lower().split(",") if t.strip()}
    valid = {"images", "videos", "audio", "cover"}
    unknown = want - valid
    if unknown:
        log.warning("忽略未知资源类型：%s", ", ".join(sorted(unknown)))
        want &= valid

    # 1. 抓取文章
    log.info("正在抓取文章：%s", args.url)
    try:
        html_text, final_url = fetch_article(args.url, timeout=args.timeout)
    except Exception as exc:
        log.error("%s", exc)
        return 2

    # 2. 解析
    ap = ArticleParser(html_text, args.url, final_url)
    meta = ap.parse_meta()
    log.info("标题：%s", meta.title)
    log.info("公众号：%s | 发布时间：%s", meta.account or "未知", meta.publish_time or "未知")

    resources: list[Resource] = []
    idx = 0

    if "cover" in want and meta.cover_url:
        idx += 1
        resources.append(Resource(index=0, rtype="image", url=meta.cover_url, origin="cover",
                                  extra={"is_cover": True}))

    if "images" in want:
        for item in ap.parse_images():
            idx += 1
            resources.append(Resource(index=idx, rtype="image", url=item["url"], origin="data-src",
                                      extra={"alt": item["alt"]}))

    if "videos" in want:
        for item in ap.parse_videos():
            idx += 1
            resources.append(Resource(index=idx, rtype="video", url=item.get("play_url") or item.get("embed_url", ""),
                                      origin=item.get("source", ""), extra=item))

    if "audio" in want:
        for item in ap.parse_audios():
            idx += 1
            resources.append(Resource(index=idx, rtype="audio", url=item["url"], origin=item.get("source", ""),
                                      extra=item))

    if args.limit and "images" in want:
        img_res = [r for r in resources if r.rtype == "image" and not r.extra.get("is_cover")]
        for r in img_res[args.limit:]:
            r.status = "skipped"
            r.error = f"超过 --limit {args.limit}"

    n_img = sum(1 for r in resources if r.rtype == "image" and r.status != "skipped")
    n_vid = sum(1 for r in resources if r.rtype == "video")
    n_aud = sum(1 for r in resources if r.rtype == "audio")
    log.info("资源发现：图片 %d | 视频 %d | 音频 %d", n_img, n_vid, n_aud)

    if args.list_only:
        for r in resources:
            print(f"[{r.rtype:5}] {r.url[:120]}")
        return 0

    # 3. 输出目录
    if args.output:
        out_dir = Path(args.output)
    else:
        out_dir = Path("downloads") / sanitize_filename(meta.title)
    out_dir.mkdir(parents=True, exist_ok=True)
    for sub in ("images", "videos", "audio"):
        (out_dir / sub).mkdir(exist_ok=True)
    log.info("输出目录：%s", out_dir.resolve())

    if args.save_html:
        (out_dir / "article.html").write_text(html_text, encoding="utf-8")
        log.info("已保存文章 HTML：article.html")

    # 4. 下载
    session = build_session(args.ua)
    session.headers["Referer"] = final_url or args.url
    dl = ResourceDownloader(session, referer=final_url or args.url, timeout=args.timeout, retries=args.retries)
    print_lock = __import__("threading").Lock()
    pending = [r for r in resources if r.status == "pending"]

    def original_variant(url: str) -> Optional[str]:
        return MMBIZ_FMT_SEGMENT.sub(r"\1/0\2", url) if args.original else None

    def work(r: Resource) -> None:
        time.sleep(args.delay)
        try:
            url = r.url
            data, ctype = dl.fetch_bytes(url)
            if args.original and r.rtype == "image":
                ov = original_variant(url)
                if ov:
                    try:
                        data, ctype = dl.fetch_bytes(ov)
                        url = ov
                    except Exception:
                        pass  # 原图失败则回退已下载的 web 图
            ext = sniff_extension(data[:32], ctype, url)
            fname = f"{r.index:03d}_{short_hash(url)}{ext}"
            sub = {"image": "images", "video": "videos", "audio": "audio"}[r.rtype]
            if r.extra.get("is_cover"):
                fname = f"cover{ext}"
                dest = out_dir / fname
            else:
                dest = out_dir / sub / fname
            dest.write_bytes(data)
            r.local_path = str(dest.relative_to(out_dir)).replace("\\", "/")
            r.size = len(data)
            r.status = "ok"
            with print_lock:
                print(f"  [{r.index:03d}] {r.rtype:5} {human_size(len(data)):>9}  {r.local_path}")
        except Exception as exc:
            # 视频解析失败时保留元数据
            if r.rtype == "video":
                r.status = "metadata_only"
                r.extra.setdefault("embed_url", r.url)
            else:
                r.status = "failed"
            r.error = str(exc)
            with print_lock:
                print(f"  [{r.index:03d}] {r.rtype:5} 失败: {exc}")

    if pending:
        with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
            futures = [pool.submit(work, r) for r in pending]
            for _ in as_completed(futures):
                pass

    # 5. 清单与报告
    manifest = {
        "article": {
            "url": meta.url,
            "final_url": meta.final_url,
            "title": meta.title,
            "account": meta.account,
            "publish_time": meta.publish_time,
            "cover_url": meta.cover_url,
            "fetched_at": meta.fetched_at,
        },
        "stats": {
            rtype: {
                "total": sum(1 for r in resources if r.rtype == rtype),
                "ok": sum(1 for r in resources if r.rtype == rtype and r.status == "ok"),
                "failed": sum(1 for r in resources if r.rtype == rtype and r.status not in ("ok", "skipped")),
                "bytes": sum(r.size for r in resources if r.rtype == rtype and r.status == "ok"),
            }
            for rtype in ("image", "video", "audio")
        },
        "resources": [
            {
                "index": r.index,
                "type": r.rtype,
                "url": r.url,
                "origin": r.origin,
                "extra": r.extra,
                "local_path": r.local_path,
                "size": r.size,
                "status": r.status,
                "error": r.error,
            }
            for r in resources
        ],
    }
    (out_dir / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    if not args.no_report:
        build_report(meta, resources, out_dir, out_dir / "index.html")

    # 6. 汇总
    ok_count = sum(1 for r in resources if r.status == "ok")
    total_bytes = sum(r.size for r in resources if r.status == "ok")
    print()
    log.info("下载完成：成功 %d / 共 %d，合计 %s", ok_count, len(resources), human_size(total_bytes))
    log.info("资源清单：%s", (out_dir / "manifest.json").resolve())
    if not args.no_report:
        log.info("可视化报告：%s", (out_dir / "index.html").resolve())
    return 0


if __name__ == "__main__":
    sys.exit(main())
