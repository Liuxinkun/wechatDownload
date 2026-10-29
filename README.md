# 微信公众号文章资源爬取下载

一个用于把微信公众号文章及其内嵌**图片 / 视频 / 音频 / 封面**抓取并批量下载到本地的工具集合。

本项目提供两种使用方式，底层抓取与解析逻辑一致：

| 方式 | 技术栈 | 适用场景 |
|------|--------|----------|
| **Python 脚本** | `requests` + `beautifulsoup4` + `lxml` | 命令行、服务器、自动化批处理 |
| **C# 桌面端** | Avalonia 11 + SukiUI（.NET 8） | Windows 图形界面，交互式下载 |

---

## ✨ 功能特性

- **文章抓取**：自动处理 `/s/` 短链跳转；内置多组微信 UA 候选链 + Cookie 会话，规避 MMLAS 风控验证页。
- **元数据解析**：标题、公众号名称、发布时间、封面图地址。
- **资源提取与下载**：
  - 图片（`data-src` 懒加载、背景图、封面，支持**原图模式** `mmbiz/0`）
  - 视频（腾讯视频 iframe `vid`、公众号原生 `mpvideo`、视频号链接）
  - 音频（`mpvoice`）
- **可视化报告**：生成 `index.html` 资源预览页 + `manifest.json` 清单。
- **并发下载**：可配置线程数（1–16），带重试与超时。
- **桌面端额外能力**：深色/浅色主题切换、下载历史记录、设置持久化、进度条与取消、一键打开输出目录/原文。

---

## 📁 项目结构

```
公众号文章资源爬取/
├── wechat_resource_downloader.py   # Python 命令行工具
└── WechatArticleDownloader/        # C# / Avalonia 桌面客户端（.NET 8）
    ├── App.axaml(.cs)              # 应用入口与样式
    ├── Models/                     # 数据模型（ArticleInfo / ResourceItem / AppSettings）
    ├── Services/                   # 抓取 / 解析 / 下载 / 设置 / CLI 入口
    │   ├── WechatArticleService.cs  # 抓取 + 解析（多 UA 链、风控规避）
    │   ├── DownloadService.cs       # 并发下载、进度回调
    │   ├── SettingsService.cs       # 设置读写
    │   └── CliRunner.cs             # 命令行模式入口（--cli）
    ├── ViewModels/                 # MVVM 视图模型（ReactiveUI）
    ├── Views/                      # 界面（Home / History / Setting / About）
    └── WechatArticleDownloader.csproj
```

> 仓库已通过 `.gitignore` 排除构建产物（`bin/`、`obj/`、`publish/`）、下载目录、`Downloads` 与 IDE 配置，**仅保留源码**。

---

## 🐍 方式一：Python 脚本

### 安装依赖

```bash
pip install requests beautifulsoup4 lxml
```

### 基本用法

```bash
# 下载一篇文章的图片/视频/音频/封面，输出到 downloads/<标题>/
python wechat_resource_downloader.py https://mp.weixin.qq.com/s/xxxxxx

# 尝试下载原图
python wechat_resource_downloader.py <URL> --original

# 仅解析并列出资源，不下载
python wechat_resource_downloader.py <URL> --list-only

# 只下载前 10 张图片
python wechat_resource_downloader.py <URL> --types images --limit 10

# 自定义输出目录、并发数、保存原始 HTML
python wechat_resource_downloader.py <URL> -o ./out --workers 8 --save-html
```

### 常用参数

| 参数 | 说明 |
|------|------|
| `url` | 文章链接（支持 `/s/` 短链） |
| `-o, --output` | 输出目录（默认 `downloads/<标题>`） |
| `--types` | 资源类型：`images,videos,audio,cover`（默认全部） |
| `--workers` | 并发下载线程数（默认 4） |
| `--delay` | 每个资源下载前间隔秒数（默认 0.15） |
| `--retries` | 单个资源重试次数（默认 3） |
| `--limit` | 最多下载图片数（0 为不限） |
| `--original` | 尝试下载原图（失败自动回退） |
| `--save-html` | 同时保存文章原始 HTML |
| `--no-report` | 不生成 `index.html` 报告 |
| `--list-only` | 仅解析列出，不下载 |
| `--ua` / `-v` | 自定义 UA / 详细日志 |

### 输出

```
<输出目录>/
├── images/      # 图片（封面为 cover.jpg）
├── videos/      # 视频
├── audio/       # 音频
├── article.html # （--save-html 时）原文 HTML
├── manifest.json# 资源清单
└── index.html   # 可视化报告
```

---

## 🖥️ 方式二：C# 桌面端（Windows）

### 构建与运行

需要 **.NET 8 SDK**。在项目根目录执行：

```bash
cd WechatArticleDownloader
dotnet build -c Release
dotnet run -c Release
```

发布为单文件可执行程序：

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

### 使用流程

1. 在首页输入框粘贴文章链接；
2. 点击「获取信息」→ 自动抓取并解析，显示标题、公众号、资源数量与封面预览；
3. 勾选要下载的资源类型（图片/视频/音频/封面）、是否原图、是否保存 HTML、并发数；
4. 点击「开始下载」→ 进度条实时更新，可随时「取消」；
5. 完成后可在「历史」页查看，或一键打开输出目录。

### 命令行模式

桌面端也支持无界面调用，便于脚本集成（复用同一套服务层）：

```bash
WechatArticleDownloader.exe --cli <URL>            # 仅解析并打印资源清单
WechatArticleDownloader.exe --cli <URL> --download # 下载全部资源
WechatArticleDownloader.exe --cli <URL> --download --out D:\out
```

---

## ⚠️ 注意事项与免责声明

- 微信对文章抓取有**反爬与风控机制**，本工具通过 UA 伪装与重试尽量规避，但仍可能因 IP 限制、链接失效或验证页而失败，请合理控制频率。
- 下载的内容**仅供个人学习、备份与存档使用**，请遵守相关平台条款与版权法规，**勿用于商业用途或批量侵犯他人权益**。
- 视频（尤其腾讯视频、视频号）的真实播放地址可能随平台策略变化而失效，工具会保留可访问链接供手动获取。

---

## 🧩 技术栈

- **Python**：`requests`、`beautifulsoup4`、`lxml`
- **C# / 桌面端**：.NET 8、Avalonia 11.3、SukiUI 6、ReactiveUI、Material.Icons.Avalonia、HtmlAgilityPack

---

## 📄 License

仅供个人学习与备份用途。
