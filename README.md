# BiliFansDisplay

一个简单的 WinUI 3 桌面小工具，用来显示 Bilibili 粉丝数和 YouTube 频道订阅数。

## 功能

- 通过 `settings.ini` 配置 Bilibili 空间链接和 YouTube 频道链接。
- 每 3 分钟刷新一次粉丝数，并记录时间戳。
- 显示最近 1h、3h、24h 的滚动粉丝变化。
- Bilibili 和 YouTube 可以同时显示为两个独立窗口。
- 右键窗口可以立刻刷新，也可以切换亚克力背景风格。
- 常驻系统托盘，不占用任务栏；托盘菜单可以显示、隐藏、刷新、打开配置或退出程序。
- 每个跟踪链接使用独立日志文件，并自动迁移旧版 Bilibili 日志。
- 启动时清理 3 天以前的历史记录。

## 本地数据

应用是 unpackaged WinUI 3 exe，配置和日志保存在：

```text
%LOCALAPPDATA%\BiliFansDisplay
```

主要文件：

```text
settings.ini
config.json（旧版 UID 迁移来源）
history\<uid>.json
history\youtube-<hash>.json
```

旧版 `fans-history.json` 会在 UID 配置完成后迁移到对应的 `history\<uid>.json`。

`settings.ini` 示例：

```ini
[Bilibili]
Url=https://space.bilibili.com/23940738

[YouTube]
Url=https://www.youtube.com/@YouTube
```

支持的 YouTube 链接格式包括：

```text
https://www.youtube.com/@handle
https://www.youtube.com/channel/UC...
https://www.youtube.com/c/name
https://www.youtube.com/user/name
```

## 构建

```powershell
dotnet build .\BiliFansDisplay\BiliFansDisplay.csproj -c Release -p:Platform=x64
```

构建后的 exe 位于：

```text
BiliFansDisplay\bin\x64\Release\net9.0-windows10.0.26100.0\win-x64\BiliFansDisplay.exe
```

## 安装和开机自启动

运行安装脚本会构建 Release x64 版本，生成本地 zip 包，安装到当前用户目录，并创建开始菜单和开机自启动快捷方式：

```powershell
.\scripts\Install-BiliFansDisplay.ps1
```

生成的本地包位于：

```text
artifacts\BiliFansDisplay-win-x64.zip
```

默认安装路径：

```text
%LOCALAPPDATA%\Programs\BiliFansDisplay
```

开机自启动使用当前用户 Startup 文件夹：

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\BiliFansDisplay.lnk
```
