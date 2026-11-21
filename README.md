# PCL Fix

> PCL Fix 是基于 [Plain Craft Launcher 2（PCL2）](https://github.com/Meloong-Git/PCL) 开源代码维护的第三方分支。
> 保留原版全部功能，并针对「下载」做了解限与增强。

[![Build PCL Fix](https://github.com/AxionMC/PCL-Fix/actions/workflows/build.yml/badge.svg)](https://github.com/AxionMC/PCL-Fix/actions/workflows/build.yml)

## 这个分支改了什么

### 1. 品牌更名

- 项目名、窗口标题、产品信息统一为 **PCL Fix**
- 程序集名为 `PCLFix`（避免 WPF pack URI 在含空格时解析失败），构建产物 `PCLFix.exe`，CI 分发时重命名为 `PCL Fix.exe`
- 资源包 URI、XAML 命名空间引用同步修正

### 2. 解除下载限制

| 原版行为 | PCL Fix 行为 |
| --- | --- |
| 开源分支通过 `RestrictMcDownload` 直接禁用 Minecraft 下载入口 | 已关闭该限制，下载页、安装页、整合包下载、更新提示等全部恢复 |
| 下载速度上限由滑块控制（`ToolDownloadSpeed`） | 限速逻辑整体移除，恒为不限速（`NetTaskSpeedLimitHigh = -1`） |
| `meloong.com / bmclapi / github.com / optifine.net / momot.rs / pcl2-server` 强制单线程 | 白名单式单线程限制已删除，所有源都会尝试分段多线程 |
| 最大线程数 = 设置值 + 1，默认 63（即 64） | 直接使用设置值，默认 127，滑块上限 255 |
| 分段最小碎片 256 KB | 降低到 128 KB，更多文件可以并行分段 |

> 注：下载源自身不支持 `Range` 时，引擎会自动回退到单线程/整文件重试，不会因此下载失败。

### 3. 新增 aria2c（Aria2）多线程下载引擎

- 新增 `Modules/Base/ModAria2.vb`：本地 `aria2c` 的 JSON-RPC 客户端与进程管理
- 并发/分段线程上限提升到 **128**（`split=128`、`max-concurrent-downloads=128`、`bt-max-peers=128`；aria2 对单个服务器仍硬限制 16 条连接，多源/多任务共用 128 条）
- 文件大小 **≥ 1 MB** 时，PCL 的探测线程会把整个文件交接给 aria2 下载：
  - 真正的分段并发（`split` / `max-connection-per-server`，上限 16）
  - 多下载源自动回退（`aria2.addUri` 直接接收全部镜像 URL）
  - 断点续传、连接复用、磁盘缓存
  - 实时进度与速度回写到 PCL 的下载管理器，任务页面显示正常
- aria2 不可用或下载失败时，自动回退到 PCL 内置多线程引擎，不会中断任务
- 首次启用时若本地没有 `aria2c.exe`，会依次尝试官方 Release 与多个 GitHub 镜像自动下载
- 预置 `--stop-with-process`，启动器退出时 aria2c 一起退出，不留后台进程

#### 设置位置

`设置 → 系统 → 下载`：

- **Aria2 多线程**：开关（`ToolDownloadAria2`）
- **Aria2 分段线程数**：1–16（`ToolDownloadAria2Thread`，默认 16）
- **Aria2 程序路径**：留空则自动查找/自动下载（`ToolDownloadAria2Path`）

### 4. 界面重做（现代化）

- 标题栏向量 PCL 字形换成 **PCL Fix** 文字标识（不再显示 "PCL"）
- 默认窗口 **1200×780**，最小 **1040×660**；旧版本保存过小的尺寸会自动升级
- 窗口大圆角 **26px**（`RectForm` 圆角裁剪）
- **流体云背景**：3 个缓慢漂移的半透明模糊色块，`SineEase` 往返动画
- **在线二次元壁纸**：启动时从 `t.alcy.cc` / `loliapi.com` / `anosu.top` 依次尝试获取随机动漫壁纸，支持 webp，缓存到 `PCL\Pictures\OnlineWallpaper.img`
  - 若用户自己在 `PCL\Pictures` 放了图，优先使用本地图
  - 设置 → 个性化：新增「启动时从网络获取二次元壁纸」开关与「换一张网络壁纸」按钮

### 5. BT 种子 / 磁力下载器

- 新增 `Modules/Base/ModTorrent.vb` + 窗口 `Pages/PageOther/FrmTorrent.xaml`（大圆角、卡片式任务列表）
- 入口：**设置 → 系统 → 下载 → 打开 BT 种子下载器**
- 支持粘贴磁力链接、选择 `.torrent` 文件、直接拖拽种子进窗口
- 走 aria2 的 BitTorrent 引擎：`bt-max-peers=128`、DHT/LPD/PEX、`seed-time=0`（下载完不做种）
- 每个任务显示进度、速度、连接数，可暂停 / 继续 / 打开文件夹 / 删除
- 保存目录可在窗口内更改（`ToolTorrentFolder`，默认 `PCL\BT`）

### 6. GitHub Actions 自动构建

`.github/workflows/build.yml`：

1. 检出仓库并递归拉取 `MeloongCore` 子模块
2. 拉取最新帮助文档并打包为 `Resources/Help.zip`
3. 注入 `CLIENT_ID` / `CURSEFORGE_API_KEY` / `CommitHash`（无密钥时自动跳过）
4. MSBuild 构建 `PCL Fix\PCL Fix.vbproj`（Release / AnyCPU）
5. 下载 aria2c 1.37.0 并放入产物目录
6. 打包 `dist/` 并上传 artifact：`PCL-Fix-<tag 或 commit>`

推送 tag 或手动 `workflow_dispatch` 均可触发。

## 本地构建

环境要求：

- Windows 10/11
- Visual Studio 2022（含 **.NET Framework 4.8 开发工具包** 与 **.NET 桌面开发** 工作负载）
- Git（用于拉取子模块）

```powershell
git clone --recursive https://github.com/AxionMC/PCL-Fix.git
cd PCL-Fix
# 若忘记 --recursive：
git submodule update --init --recursive

msbuild "PCL Fix\PCL Fix.vbproj" -p:Configuration=Release -p:Platform=AnyCPU
```

产物位于 `PCL Fix\obj\Release\`（或 `PCL Fix\bin\`）中的 `PCLFix.exe`，把 `aria2c.exe` 放在它同目录即可使用 Aria2 引擎。

## 目录结构

```
PCL Fix.sln                解决方案
PCL Fix\                   VB.NET + WPF 主工程
  Modules\Base\ModNet.vb     下载引擎（已解限 + Aria2 接管）
  Modules\Base\ModAria2.vb   aria2c JSON-RPC 客户端（新增）
  Modules\ModSecret.vb       分支标识与功能开关（RestrictMcDownload = False）
  Pages\PageSetup\          设置页（含 Aria2 相关控件）
PCLCS\                     C# 辅助库
MeloongCore\               子模块
.github\workflows\build.yml 自动构建
```

## 致谢与许可

- 原始项目：[Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)，作者 龙腾猫跃 及社区贡献者
- aria2：[aria2/aria2](https://github.com/aria2/aria2)，GPLv2
- 本仓库沿用原仓库的 [LICENCE](LICENCE)（《PCL 分发有限许可》与《PCL 存储库合理使用指南》），请在使用与分发前阅读
- PCL Fix 与 PCL 官方无隶属关系，仅为社区分支