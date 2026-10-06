# MusicTagRecover

音乐标签批量管理工具 —— 基于 WPF 重构的桌面版音乐元数据编辑器。

> 本项目派生自 [lkjo1989/musictag-clone](https://github.com/lkjo1989/musictag-clone)（MIT License），
> 功能灵感来自「音乐标签 / music-tag-web」生态，代码为独立的 C# / WPF 实现。

## 功能特性

- **批量标签编辑**：标题、艺术家、专辑、专辑艺术家、作曲、作词、流派、年份、音轨/碟号、评论等字段的批量查看与修改
- **自动匹配标签**：从多个在线标签源批量刮削标题/专辑/艺术家/歌词/封面，支持匹配度筛选与自定义搜索条件
- **歌词工具**：内嵌歌词编辑、歌词源在线搜索、LRC 文件导入/导出（支持 UTF-8/GBK/Big5/Unicode 编码选择）
- **封面管理**：封面在线搜索、批量嵌入/提取、多封面（封面/封底/内页等类型）支持
- **编码修复**：一键修复 GBK/Big5 等历史编码导致的标签乱码
- **文件名联动**：按自定义模板批量重命名文件，或从文件名反向提取标签
- **文件比对**：目录快照比对、删除历史追踪与回收站恢复
- **内置试听**：FFmpeg 解码内核（含 APE 专用解码），列表内直接预览播放
- **高性能扫描**：MediaInfo 驱动的轻量读取管线，千级文件目录秒级完成扫描（详见 `docs/标签读取性能优化技术文档.md`）

### 支持的格式

FLAC、APE、WAV、AIFF、WV、TTA、MP3、MP4/M4A、OGG、OPUS、MPC、WMA、DSF、DFF 等常见音频格式；
标签写入支持 ID3v1/v2、APE Tag、Vorbis Comment、MP4、ASF 等容器。

## 界面预览

<!-- 发布后请在此处补充应用截图，例如：
![主界面](docs/screenshots/mainwindow.png)
-->

## 系统要求

- Windows 10 / 11（x64）
- 免安装：下载 Release 压缩包解压即用，无需 .NET 运行时

## 下载与安装

1. 前往 [Releases](../../releases) 页面下载最新的 `MusicTagRecover-*-win-x64.zip`
2. 解压到任意目录，运行 `MusicTagRecover.exe`
3. 所有依赖（含 FFmpeg 解码内核）已随包提供

## 从源码构建

要求：.NET SDK 10.0+

```powershell
git clone https://github.com/<your-account>/MusicTagRecover.git
cd MusicTagRecover
dotnet build -c Release
```

输出位于 `bin\Release\net10.0-windows\`，直接运行其中的 `MusicTagRecover.exe` 即可。
构建脚本会自动裁剪与 Windows x64 无关的原生运行时资产（约 500MB 的 Linux/macOS 变体不会进入输出目录）。

## 使用说明

详细操作指南见 [使用说明.md](使用说明.md)。

## 技术架构

- **UI**：WPF（MVVM，CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection）
- **标签读取**：MediaInfo（轻量扫描）+ TagLibSharp2 / TagLib#（全量读取与回退），三级回退链保证格式覆盖
- **标签写入**：TagLibSharp2 优先、TagLib# 回退，带文件占用重试与只读属性处理
- **播放内核**：FFmpeg 9（FFmpeg.AutoGen P/Invoke）+ Monkey's Audio SDK（APE），NAudio 输出
- **持久化**：SQLite（目录历史、比对快照、标签历史）+ JSON（应用设置）

技术细节请参阅 `docs/` 目录下的设计文档。

## 第三方组件与许可

本项目使用了多个开源组件，各自的许可条款见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
随 Release 分发的 FFmpeg 二进制采用 GPL v3 许可（gyan.dev full build），其许可文本已包含在发行包的 `ffmpeg\` 目录中。

## 许可证

[MIT License](LICENSE) © 2026 lkjo1989 & MusicTagRecover contributors

## 致谢

- [lkjo1989/musictag-clone](https://github.com/lkjo1989/musictag-clone) —— 本项目直接派生来源
- [xhongc/music-tag-web](https://github.com/xhongc/music-tag-web) —— 「音乐标签」功能设计参考
- TagLib、MediaInfo、FFmpeg 等开源社区
