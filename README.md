# TimeTracker

Windows 端「各应用使用时长」统计工具。托盘常驻，一颗电源键控制开始/停止。

## 界面

- **未开始**：一块较高的面板，正中一颗大的电源键，点它开始记录。
- **记录中**：面板自动收成内容高度（应用几条就多高），电源键平移到右上角缩成一枚小圆钮，下面按用时从多到少列出今天的应用，每条前面是应用图标。
- 标题下方会多一行 `挂机 32m`，表示今天有鼠标键盘没动、被自动暂停的时长。
- 列表超过「可视监控应用数」之后，剩下的合并成一行「其他」（数据仍在库里，只是合并显示）。
- **左上角的钉子**：点一下面板置顶，再点取消；钉子变绿就是置顶状态。
- **拖边框改大小**：鼠标移到窗口四边或四角，指针变成双向箭头，拖动即可。手动调过尺寸后，面板不再自动收放高度，直到重启。
- **关窗口 = 缩回托盘**（`Esc` 同样），真要退出用托盘右键菜单的「退出」。
- 托盘左键点图标打开面板，右键菜单可开关记录、切换开机自启、打开「设置」。
- **设置**里可以切换白天/夜间主题、调整可视应用数（1–30，超过 30 显示为「不限」）。
- 面板可以按住空白处拖动摆放；再双击一次 exe 或快捷方式，会把已经开着的面板唤到前面（不会双开）。

> Windows 11 默认把新图标收进托盘溢出区（任务栏那个 `^`）。第一次找不到图标就点一下 `^`，
> 把 TimeTracker 拖到常显位置。

## 目录结构

| 文件 | 职责 |
|---|---|
| `Native.cs` | 全部 Win32 P/Invoke 声明，集中管理 |
| `Foreground.cs` | 判定「当前前台是哪个应用」，含 UWP 特殊处理、空闲时长、锁屏检测 |
| `SessionStore.cs` | SQLite 存储与统计查询（区间求交，支持跨天会话；另有 `idle` 表存挂机段），内部加锁串行化 |
| `Tracker.cs` | 采样主循环，会话开始/结束/滚动落库，对外暴露当前会话与挂机起点 |
| `TrayApp.cs` | 托盘图标、圆角右键菜单、开机自启、采集器启停、设置窗口的宿主 |
| `MainForm.cs` | 无边框自绘主面板：主题配色、电源键动画、应用列表、钉子、边框拖拽 |
| `SettingsForm.cs` | 自绘设置窗口：白天/夜间、可视应用数 |
| `AppSettings.cs` | `settings.json` 读写（主题、可视应用数、置顶） |
| `AppIcons.cs` | 从 exe 路径抽图标并缓存，取不到就画首字母色块 |
| `Report.cs` | 控制台统计报表（托盘版不再调用，留作调试参考） |
| `Options.cs` | 命令行参数 |
| `Program.cs` | 入口（单实例互斥） |

## 环境要求

- Windows 10 1809+ / Windows 11
- .NET 8 SDK（`dotnet --version` 应为 8.x）

## 运行

```powershell
cd TimeTracker
dotnet run
```

启动后没有黑框，只在屏幕右下角托盘出现图标，默认**自动开始记录**。

首次运行会自动创建数据库：`%LOCALAPPDATA%\TimeTracker\timetracker.db`

设置存在同目录的 `settings.json` 里（纯文本，可以直接改，改完重启生效）：

```json
{ "Dark": true, "VisibleApps": 5, "TopMost": false }
```

- `Dark`：`true` 夜间、`false` 白天
- `VisibleApps`：列表里最多点名几个应用，`0` 表示不限
- `TopMost`：面板是否置顶（等同点左上角那颗钉子）

## 常用参数

| 参数 | 说明 |
|---|---|
| `--idle N` | 空闲超过 N 秒停止计时（默认 60） |
| `--sample N` | 采样间隔毫秒（默认 1000） |
| `--db <path>` | 指定数据库位置 |
| `--panel` | 启动后直接把面板摊开（默认只在托盘） |

调试小工具：环境变量 `TT_DEBUG=1` 启动时，会往 `%TEMP%\tt-debug.log` 记录每次点击的命中情况，平时不写任何文件。

## 验证清单（重要）

跑满一天后，重点核对这几件事，它们是最容易出错的地方：

1. **UWP 应用**：打开「计算器」「设置」「照片」，面板里应显示 `CalculatorApp.exe` / `SystemSettings.exe` / `Microsoft.Photos.exe`，
   而不是全部显示 `ApplicationFrameHost.exe`。
2. **锁屏**：Win+L 锁屏 1 分钟，回来检查这段时间没有被计入。
3. **离开**：放着不动超过 `--idle` 秒，同样应暂停计时（面板上的「挂机」一行开始增加）。
4. **睡眠**：合盖/休眠几分钟后唤醒，这段时间不应被计入。
5. **看视频/全屏游戏**：全屏程序也应被正常记录。
6. **跨天**：23:50 用浏览器到 0:30，第二天看面板，昨天应只算到 23:59，今天从 0:00 开始。
7. **总时长**：面板顶部的今日合计应明显小于「开机时长」，否则说明空闲判定没生效。
8. **图标**：列表里的图标应该和任务栏上的一致；UWP 应用取不到图标时会退化成首字母色块，属正常。
9. **其他**：把「可视应用数」调到 1，列表应只剩第一名 + 「其他」，其余数据仍在库里。

## 打包成独立 EXE

**双击 `build.cmd`** 即可，产物在 `dist\TimeTracker.exe`：

- 单文件，自带 .NET 运行时，**目标机器不需要装任何东西**
- 体积约 60 MB（含 WinForms 与 SQLite 原生库；压缩参数对自解压包效果有限）
- SQLite 原生库会随首次启动解压到 `%TEMP%\.net\`，属正常现象

等价的手工命令：

```powershell
dotnet publish TimeTracker.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

几个关键参数的作用：

| 参数 | 作用 |
|---|---|
| `--self-contained true` | 把 .NET 运行时打进 EXE，这是「不依赖系统软件」的关键 |
| `-r win-x64` | 指定目标平台，必须和 self-contained 一起用 |
| `PublishSingleFile` | 所有托管 DLL 合并成一个文件 |
| `IncludeNativeLibrariesForSelfExtract` | **必须加**，否则 `e_sqlite3.dll` 不会被打进去，运行时报找不到 SQLite |
| `EnableCompressionInSingleFile` | 压缩托管资源，启动会慢一点点 |

## 常见问题

### `UnauthorizedAccessException: Access to the path '...\AppData\Local\TimeTracker' is denied`

系统层面拒绝了目录创建，不是 SQLite 的问题。排查：

```powershell
$p = "$env:LOCALAPPDATA\TimeTracker"
Test-Path $p                                  # 是否存在
Get-Item $p -Force | fl FullName, Attributes  # 是文件还是目录
icacls $p                                     # 看 ACL
New-Item -ItemType Directory "$env:LOCALAPPDATA\probe_$(Get-Random)" -ea Stop   # 能否建目录
```

三种典型原因：同名**文件**占用、目录 ACL 异常、安全软件拦截。临时绕过：

```powershell
dotnet run -- --db "$env:USERPROFILE\Documents\TimeTracker\timetracker.db"
```

程序自带兜底：默认位置不可写时会自动退到 `%APPDATA%\TimeTracker`，再退到 exe 旁的 `data\`，并打印提示。

## 已知限制

- **浏览器**只统计到 `chrome.exe` 这一层，不区分网页。要做到标签页粒度，需要读浏览器标题（能做，但标题格式各浏览器不同）或写扩展，属于二期。
- **提权窗口**（以管理员身份运行的程序）的标题可能读不到，但进程名仍能拿到，计时不受影响。
- 只统计前台应用，不统计后台运行。
- 面板里的实时数字每 1.5 秒刷新一次，切换瞬间可能有一两秒的滞后。
- 挂机段短于 20 秒不落库（避免每点一下鼠标就写一行）。
- 手动拖过边框之后，面板高度就固定了，不再随应用条数自动收放。

## 下一步路线

1. **历史视图**：现在只显示今天，可按周/月聚合，加一个日历式热力图。
2. **分类**：新增 `category` 表（exe_name → 工作/娱乐/中性），报表按分类汇总。
3. **清理**：超过 90 天的明细自动归档成按天汇总，控制数据库体积。
4. **挂机报表**：把 `idle` 表也做进历史视图，看「今天在座多久、真正用了多久」。
