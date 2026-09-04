# 开发说明

## 项目结构

| 目录 | 内容 |
| --- | --- |
| BiliLocal.Core | Bilibili API、登录、视频筛选、下载和持久化队列 |
| BiliLocal.Desktop | Avalonia 桌面界面、图标 |
| BiliLocal.Tests | 使用模拟网络和合成凭据的自动化测试 |
| BiliLocal.Diagnostics | 手动联网诊断工具 |
| vendor/ffmpeg | FFmpeg 许可证、版本及来源说明 |
| docs/images | README 示例截图 |

## 构建和验证

需要 Windows x64、.NET 8 SDK 和 PowerShell。首次构建先执行 `./setup-ffmpeg.ps1`，下载固定的 FFmpeg 8.0.1 full build，并校验压缩包和可执行文件的 SHA256。大文件保留在本机，不进入 Git。

```powershell
.\setup-ffmpeg.ps1
dotnet build BiliLocal.sln -c Release
dotnet test BiliLocal.Tests -c Release
dotnet run --project BiliLocal.Desktop -- --smoke
.\publish.ps1 -SingleFile
```

单文件发布输出 `dist/BiliLocal.exe` 及 SHA256 校验文件，包含 .NET 运行时、FFmpeg 和浏览器登录驱动，首次运行自动释放依赖。省略 `-SingleFile` 则输出到 `publish/win-x64`，分发时需保留整个目录。账号登录使用本机 Edge / Chrome。

`build-icon.ps1` 根据 `BiliLocal.Desktop/Assets/BiliLocal.png` 重新生成多尺寸 ICO；图标生成说明见同目录 `GENERATION.md`。

## 数据和隐私

在源码目录运行时，默认下载到 `downloads/`，设置、任务记录和续传缓存保存在 `data/`。独立 EXE 在项目外运行时使用 `%LOCALAPPDATA%/BiliLocal`；可通过环境变量 `BILILOCAL_HOME` 指定数据根目录。

可在界面修改成品保存目录，新任务使用新目录，已有任务保持原目录。删除队列任务不会删除已下载的视频。退出时暂停任务，重启后可手动继续。

Cookie 仅保存在内存，不写入任务记录。不要提交 Cookie、浏览器配置、个人数据或下载文件；`data/`、`downloads/`、`artifacts/`、构建和发布目录已加入忽略规则。

## 第三方组件

FFmpeg 以独立进程运行，使用未经修改的 [Gyan 8.0.1 full build](https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1)。其 [许可证](../vendor/ffmpeg/win-x64/LICENSE)、[构建说明](../vendor/ffmpeg/win-x64/README.txt)及[来源和校验值](../vendor/ffmpeg/win-x64/BUNDLED-NOTICE.txt)随软件打包。

其余依赖及固定版本见各项目的 `.csproj` 文件，包括 Avalonia、Playwright 和 QRCoder；各组件遵循各自许可证。
