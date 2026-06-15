# BiliFansDisplay

一个简单的 WinUI 3 桌面小工具，用来显示指定 B 站 UID 的粉丝数。

## 功能

- 首次启动输入 UID 并确认，之后应用内不再提供修改入口。
- 每 3 分钟刷新一次粉丝数，并记录时间戳。
- 显示最近 1h、3h、24h 的滚动粉丝变化。
- 右键窗口可以立刻刷新，也可以切换亚克力背景风格。
- 每个 UID 使用独立日志文件，并自动迁移旧版日志。
- 启动时清理 3 天以前的历史记录。

## 本地数据

应用是 unpackaged WinUI 3 exe，配置和日志保存在：

```text
%LOCALAPPDATA%\BiliFansDisplay
```

主要文件：

```text
config.json
history\<uid>.json
```

旧版 `fans-history.json` 会在 UID 配置完成后迁移到对应的 `history\<uid>.json`。

## 构建

```powershell
dotnet build .\BiliFansDisplay\BiliFansDisplay.csproj -c Release -p:Platform=x64
```

构建后的 exe 位于：

```text
BiliFansDisplay\bin\x64\Release\net9.0-windows10.0.26100.0\win-x64\BiliFansDisplay.exe
```
