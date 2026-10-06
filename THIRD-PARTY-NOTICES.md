# 第三方组件声明（Third-Party Notices）

MusicTagRecover 使用了以下第三方组件。本项目代码本身采用 [MIT License](LICENSE)，
各组件的原始许可条款如下，分发时须一并遵守。

## NuGet 托管组件

| 组件 | 版本 | 许可 | 项目地址 |
|---|---|---|---|
| TagLibSharp | 2.3.0 | LGPL-2.1 | https://github.com/mono/taglib-sharp |
| TagLibSharp2 | 0.6.0 | LGPL-2.1 | https://www.nuget.org/packages/TagLibSharp2 |
| MediaInfo.Wrapper.Core | 26.1.0 | MIT | https://github.com/iYaroslav/MediaInfo.Wrapper.Core |
| MediaInfo.Native | 26.1.0 | BSD-2-Clause（MediaInfoLib） | https://mediaarea.net/MediaInfo |
| NAudio | 3.1.0 | Ms-PL | https://github.com/naudio/NAudio |
| Newtonsoft.Json | 13.0.4 | MIT | https://github.com/JamesNK/Newtonsoft.Json |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| FontAwesome.Sharp | 6.6.0 | MIT（图标字体：CC BY 4.0 / SIL OFL） | https://github.com/awesome-inc/FontAwesome.Sharp |
| FFmpeg.AutoGen | 9.0.1.1 | MIT（仅绑定层） | https://github.com/Ruslan-B/FFmpeg.AutoGen |
| System.Data.SQLite.Core | 1.0.119 | 公有领域（Public Domain） | https://system.data.sqlite.org |
| Microsoft.Extensions.* | 10.0.12 | MIT | https://github.com/dotnet/runtime |

## 随发行包分发的原生二进制

### FFmpeg（`ffmpeg\` 目录）

- 来源：gyan.dev Windows full build（shared）
- 许可：**GNU GPL v3**（该构建包含 libx264 等 GPL 组件）
- 说明：FFmpeg 以独立共享库形式置于 `ffmpeg\` 子目录，仅提供解码功能；
  其完整许可文本（COPYING.GPLv3）已包含在该目录中，源代码可从 https://ffmpeg.org 获取。

### Monkey's Audio Library（`mac\` 目录）

- 组件：MACDll64.dll（Monkey's Audio 官方 SDK）
- 许可：Monkey's Audio Source Code License（免费使用与再分发，须保留原许可文本）
- 项目地址：https://monkeysaudio.com

### MediaInfo 原生库（`MediaInfo.dll`）

- 许可：BSD-2-Clause（MediaInfoLib）
- 项目地址：https://github.com/MediaArea/MediaInfoLib

## LGPL 组件的使用方式

TagLibSharp / TagLibSharp2 为 LGPL-2.1 组件，本项目以**独立程序集动态链接**方式使用，
未修改其源代码。根据 LGPL-2.1 条款，使用者可以：

1. 从 NuGet 获取这些组件的源代码自行替换/升级；
2. 在遵守 LGPL-2.1 的前提下再分发这些程序集。

LGPL-2.1 全文：https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
