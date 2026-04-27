# Third-party notices

## Runtime and build tools

This project uses the following runtime/build tools:

| Component | License | Purpose |
| --- | --- | --- |
| .NET / WPF | MIT and related Microsoft licenses | Windows desktop application runtime and UI framework |
| Inno Setup | Inno Setup License | Windows installer generation |

The repository does not vendor .NET or Inno Setup source code.

## External services

This app calls the following external APIs at runtime:

| Service | Purpose |
| --- | --- |
| Spotify Web API | Reads currently playing track and playback progress after user authorization |
| LRCLIB API | Searches synced/plain lyrics for the currently playing track |

This repository does not vendor Spotify or LRCLIB source code.

## User-provided Spotify Client ID

The app requires each user to provide their own Spotify Developer App Client ID. No shared Spotify application credentials are included in this repository.

---

# 第三方声明

## 运行时和构建工具

本项目使用以下运行时/构建工具：

| 组件 | 许可证 | 用途 |
| --- | --- | --- |
| .NET / WPF | MIT 及相关 Microsoft 许可证 | Windows 桌面应用运行时和 UI 框架 |
| Inno Setup | Inno Setup License | Windows 安装包生成 |

仓库不包含 .NET 或 Inno Setup 的源码。

## 外部服务

应用运行时会调用以下外部 API：

| 服务 | 用途 |
| --- | --- |
| Spotify Web API | 用户授权后读取当前播放曲目和播放进度 |
| LRCLIB API | 为当前播放曲目搜索同步歌词或纯文本歌词 |

仓库不包含 Spotify 或 LRCLIB 的源码。

## 用户自备 Spotify Client ID

应用要求每个用户自行提供 Spotify Developer App Client ID。仓库不包含共享的 Spotify 应用凭据。
