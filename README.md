<p align="right"><a href="README.md">中文</a> · <a href="README.en.md">English</a></p>

# 片语

[![Build & Release](https://github.com/bigsu/pianyu/actions/workflows/release.yml/badge.svg)](https://github.com/bigsu/pianyu/actions/workflows/release.yml)
[![Latest Release](https://img.shields.io/github/v/release/bigsu/pianyu?display_name=tag)](https://github.com/bigsu/pianyu/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-35d0b0)](#系统要求)

本地优先、键盘优先的 Windows 文本片段管理工具，用于快速保存、搜索、复制和复用提示词、命令及流程文本。

![片语主命令面板](docs/images/pianyu-main.png)

## 下载与使用

从 [Releases](https://github.com/bigsu/pianyu/releases/latest) 下载最新的 `pianyu.exe`，放到可写目录后直接运行。

- 单文件、自包含，目标电脑无需安装 .NET 或 SQLite。
- 第一次保存数据时，会在 EXE 同目录创建 `pianyu.db`。
- 升级时替换 EXE 即可，保留原来的 `pianyu.db`。
- Windows SmartScreen 可能提示“未知发布者”，因为当前构建未进行商业代码签名。

## 核心功能

- 新建、编辑、收藏、置顶、标签、软删除与撤销删除。
- SQLite FTS5 全文搜索，覆盖标题、正文和标签。
- 拼音首字母、轻微输错、常见缩写和个人搜索别名。
- 综合相关度、近期使用、复制次数、收藏、置顶和当前应用的动态排序。
- `{port=3001}` 形式的参数化模板与最近变量值。
- `空格` 复制并关闭、`Enter` 复制并保持打开、`Shift+Enter` 直接粘贴。
- 手动读取剪贴板，或主动开启限时监听；候选内容始终需要确认才会保存。
- 所有快捷键均可录制、清除和恢复，冲突时保留原快捷键。
- 深色、浅色、跟随系统三种主题。
- JSON 导入导出、SQLite 备份与恢复。
- 可选的大模型辅助层；模型离线、超时或未配置时，本地功能仍正常工作。

## 默认快捷键

| 动作 | 快捷键 |
| --- | --- |
| 显示/隐藏片语 | `Ctrl+Alt+Space` |
| 保存当前剪贴板 | `Ctrl+Alt+S` |
| 复制并关闭 | `空格` |
| 直接粘贴 | `Shift+Enter` |
| 复制并保持打开 | `Enter` |
| 新建片段 | `Ctrl+N` |
| 编辑片段 | `Ctrl+E` |
| 删除片段 | `Delete` |
| 撤销删除 | `Ctrl+Z` |

所有快捷键均可在“设置 → 快捷键”中修改。

## 隐私与数据

- 无账号、无遥测、无云同步。
- 片段、标签、快捷键和设置均保存在本地 `pianyu.db`。
- 默认不监听剪贴板，不会未经确认自动收录内容。
- 模型功能完全可选；API Key 使用 Windows DPAPI 按当前用户加密。
- 仓库和 GitHub Release 不包含用户数据库、API Key 或个人片段。

## 构建与测试

需要 Windows 10/11 和 .NET 8 SDK：

```powershell
dotnet restore Pianyu.sln
dotnet build Pianyu.sln -c Release --no-restore
dotnet test Pianyu.sln -c Release --no-build
dotnet publish src/Pianyu.App/Pianyu.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/release
```

## 工程结构

```text
src/Pianyu.Core     领域模型、搜索、排序和模板算法
src/Pianyu.App      WPF 界面、SQLite 仓储、Windows 服务和模型适配器
tests/Pianyu.Tests  单元测试、集成测试和失败回退测试
.github/workflows   Windows 构建、测试和自动发布
```

## 系统要求

- Windows 10 版本 2004 或更高版本，或 Windows 11
- x64 处理器
- Release 版本无需单独安装 .NET 或 SQLite

## 发布与安全

推送标签（例如 `v1.0.0`）会通过 GitHub Actions 构建、测试并发布自包含单文件 `片语.exe`。

请不要提交 `pianyu.db`、导出的片段数据、API Key 或其他个人内容。项目的 `.gitignore` 已排除常见本地数据和密钥文件。
