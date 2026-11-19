<div align="center">

<img src="docs/images/icon.png" width="88" alt="">

# Replay Clipper

Windows 上的游戏回放录制工具。程序在后台持续录制屏幕，按下快捷键即可把最近几分钟的画面保存为视频，用法类似游戏主机上的“保存最近的游戏画面”。

[![下载](https://img.shields.io/github/v/release/Lunafreya123/ReplayClipper?label=%E4%B8%8B%E8%BD%BD&color=4cc2ff)](https://github.com/Lunafreya123/ReplayClipper/releases/latest)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
[![许可证](https://img.shields.io/github/license/Lunafreya123/ReplayClipper?label=%E8%AE%B8%E5%8F%AF%E8%AF%81)](LICENSE)

简体中文 · [English](README.en.md)

</div>

<p align="center"><img src="docs/images/home-zh.webp" alt="Replay Clipper 首页"></p>

## 功能

### 录制
- 在后台录制所选显示器的画面和系统声音，可同时录制麦克风。
- 回放时长可选 15 分钟、30 分钟、1 小时、2 小时，或自定义为 1 至 720 分钟。超出回放时长的画面会自动删除。
- 回放缓存保存在磁盘上。「录制设置」中会按当前的分辨率、帧率和回放时长显示预计占用的空间。磁盘剩余空间低于 1 GB 时，录制自动停止。
- 使用显卡的硬件编码器（NVIDIA NVENC、AMD AMF、Intel Quick Sync），编码格式为 H.265 或 H.264。没有可用的硬件编码器时改用软件编码，「录制设置」中会给出提示。
- 分辨率可选 1080p、1440p 或显示器的原始分辨率，帧率可选 60 或 30 帧/秒。

### 保存
- 首页最多可设置 5 个保存时长，默认为 3、5、15、30 分钟。每个时长都可以修改，并可分别绑定快捷键。
- 快捷键在游戏中也能使用。保存时屏幕右上角显示提示，完成后播放提示音。
- 保存时直接拼接已录制的分段，不重新编码，画质不变。在开发用的电脑上（NVMe 固态硬盘），保存 5 分钟约需 0.2 秒，30 分钟约需 1 秒。

### 播放和裁剪
- 片段列表显示缩略图。播放速度可选 0.5×、1×、2×、4×。
- 裁剪可以精确到帧：滚动鼠标滚轮缩放时间轴，按 I / O 设置起点和终点，也可以逐帧微调。导出时不重新编码。

### 后台运行
- 关闭窗口时可以选择在后台继续运行或退出程序，也可以勾选「不再提醒」。
- 在后台运行时，程序图标显示在任务栏的通知区域。右键菜单中可以保存片段、开始或停止录制。
- 可以设置为开机启动并在后台录制。

### 其他
- 不需要安装 OBS。录制组件基于 OBS Studio 的核心库，已包含在安装包中。
- 界面语言可在中文和 English 之间切换。
- 安装不需要管理员权限。卸载时删除程序、设置和回放缓存，已保存的片段会保留。

## 截图

<table>
<tr>
<td width="50%"><img src="docs/images/slots-zh.webp" alt="时长与快捷键"><p align="center">时长与快捷键</p></td>
<td width="50%"><img src="docs/images/settings-zh.webp" alt="录制设置"><p align="center">录制设置</p></td>
</tr>
</table>

<p align="center"><img src="docs/images/trim-zh.webp" alt="播放与裁剪"><br>播放与裁剪</p>

## 安装

1. 在 [Releases](https://github.com/Lunafreya123/ReplayClipper/releases/latest) 页面下载 `ReplayClipper-Setup-x.y.z.exe`。
2. 运行安装程序。安装程序没有数字签名，Windows 可能显示「Windows 已保护你的电脑」，此时选择「更多信息」，再选择「仍要运行」。
3. 首次启动时会打开「录制设置」。选择要录制的显示器，然后选择「开始录制」。

系统要求：

- Windows 10 或 11，64 位
- WebView2 运行时（Windows 11 已自带，Windows 10 通常已随 Edge 安装）
- 建议使用支持硬件编码的 NVIDIA、AMD 或 Intel 显卡

## 默认快捷键

| 保存最近 | 快捷键 |
| --- | --- |
| 3 分钟 | `Alt + F7` |
| 5 分钟 | `Alt + F8` |
| 15 分钟 | `Alt + F9` |
| 30 分钟 | `Alt + F10` |

在首页时长方块后面选择「编辑」可以修改，最多 5 个。功能键可以单独使用，其他键需要与 Ctrl 或 Alt 组合。

## 常见问题

**保存为什么很快？**
录制时画面以 2 秒为一段写入磁盘。保存时只需把所需的分段拼接成 MP4 文件，不需要编码。

**会影响游戏帧率吗？**
视频编码由显卡上的专用编码单元完成。在开发用的电脑上，录制组件本身约占单个 CPU 核心的 7%。如果没有硬件编码器、改用了软件编码，游戏帧率会明显下降。

**占用多少磁盘空间？**
默认设置（1080p、60 帧/秒、约 16 Mbps）下，回放时长 30 分钟约占 3.5 GB，1 小时约占 7 GB。实际数值以「录制设置」中显示的为准。

**片段保存在哪里？**
默认保存在「视频」文件夹下的 Replay Clipper 文件夹中，可以在「录制设置」中更改。选择标题栏中的「打开文件夹」可以打开该文件夹。

**关闭窗口后还会继续录制吗？**
选择「后台运行」后会继续录制，快捷键也可以继续使用。在通知区域图标的菜单中选择「退出」才会关闭程序，此时尚未保存的画面会被丢弃。

## 工作原理

```mermaid
flowchart LR
    A["显示器画面<br>系统声音 / 麦克风"] --> B["录制组件<br>OBS 核心库 + 硬件编码"]
    B --> C[("磁盘上的回放缓存<br>每 2 秒一段")]
    C -- "按下快捷键" --> D["拼接为 MP4<br>不重新编码"]
    D --> E["片段列表<br>播放 / 裁剪"]
```

- **程序**（`src/`）：用 C# 编写，负责窗口、快捷键和托盘图标。界面是一个网页文件（`ui/index.html`），通过 WebView2 显示。
- **录制组件**（`engine/`）：一个没有窗口的辅助程序，调用 OBS Studio 33.0.1 的核心库采集画面和声音、进行硬件编码，并把录制内容写入磁盘上的循环缓存。程序通过命名管道与它通信，它意外退出时会被自动重新启动。通信协议见 [`src/engine/PROTOCOL.md`](src/engine/PROTOCOL.md)。
- **保存**：程序读取缓存中的分段，自行写出 MP4 文件。每次保存时，还会用 ffmpeg 转换第一段作为对照，两者必须逐字节一致；如果不一致，就改用 ffmpeg 拼接整个片段。

## 已知限制

- 目前只在 Windows 11 和 AMD 显卡上完整测试过。NVIDIA 和 Intel 编码器的参数参照 OBS 的推荐值设置，尚未在实际硬件上验证。如遇问题，请提交 [Issue](https://github.com/Lunafreya123/ReplayClipper/issues)，并附上 `data\engine\logs\engine.log`。
- 尚未测试独占全屏的游戏和 HDR 画面。
- 暂不支持 AV1 编码。

## 从源码构建

详见英文说明中的 [Building](README.en.md#building) 一节。程序用 Windows 自带的 C# 编译器编译，运行 `build.cmd` 即可。录制组件、ffmpeg 和 WebView2 等二进制文件不在仓库中，需要按说明放入相应的文件夹。安装程序用 Inno Setup 6 生成（`installer\build-installer.cmd`）。

## 许可

本项目按 [GPL-3.0](LICENSE) 许可发布，其中录制组件的辅助程序（`src/engine/`）按 GPL-2.0-or-later 许可。
安装包中附带的 OBS Studio 核心库、FFmpeg 等第三方组件的版本、许可和源代码位置，见 [`engine/licenses/THIRD-PARTY-NOTICES.txt`](engine/licenses/THIRD-PARTY-NOTICES.txt) 和 [`ffmpeg/README.txt`](ffmpeg/README.txt)。

“OBS”和“OBS Studio”是其各自所有者的商标。Replay Clipper 与 OBS 项目无关，也未获得其认可。
