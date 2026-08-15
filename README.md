# BlueScreenHelper

> WinUI 3 蓝屏诊断助手：本地解析 `.dmp` 转储文件、扫描系统健康状态、借助 AI 生成问题定位与解决方案。

支持直接解析 Windows 生成的 **MDMP 小转储**与**内核转储（PAGE/Triage 格式）**，无需安装 WinDbg；可对接任意 OpenAI 兼容 API 完成智能诊断。

---

## 功能

| 模块 | 说明 |
| --- | --- |
| **首页** | 系统关键信息概览（版本、CPU、内存、启动时间）、最近崩溃记录，双击记录查看详情 |
| **转储分析** | 解析 `.dmp` 文件，提取崩溃代码（BugCheck）、参数、异常信息、时间、系统版本、模块列表，生成分析报告 |
| **系统扫描** | 检查关键事件日志（BugCheck、WHEA、磁盘错误、内存诊断等）、SMART 健康状态、磁盘空间、崩溃转储文件、待重启状态 |
| **AI 诊断** | 将分析结果发送给 OpenAI 兼容 API，流式输出问题定位与解决方案；支持 OpenAI / DeepSeek / 智谱 / Moonshot / 通义 / SiliconFlow / Ollama 等 |
| **设置** | 配置 API 地址、密钥、模型、温度与系统提示词，存储于 `%LOCALAPPDATA%\BlueScreenHelper\settings.json` |

### 转储解析细节

- **MDMP（Minidump）**：`MDMP` 签名的用户态小转储，含异常信息、模块列表、线程栈等
- **内核转储（PAGE 格式）**：Windows 蓝屏生成的 `PAGE`/`DU64` 头部转储，支持全部类型：
  - 1 / 5：完整内存转储
  - 2 / 6：内核内存转储
  - 3：仅头部转储
  - 4：Triage 转储
  - 7：自动内存转储
- 解析 `DUMP_HEADER64` 头部字段：BugCheckCode、4 个参数、机器类型、系统版本（内部版本号）、DumpType、SystemTime、Comment
- 内置 **BugCheck 知识库**（数百个错误代码），将错误码映射为名称、类别、原因与解决方案
- 崩溃参数按错误码**逐位注解**（WinDbg `!analyze -v` 风格），如 `0xD1` 的第 1 参数为"发生故障的地址"
- 字节级解析验证与 WinDbg 头部数据同源，无需安装 WinDbg / dbgeng.dll

---

## 技术栈

| 类别 | 技术 |
| --- | --- |
| UI 框架 | **WinUI 3**（Windows App SDK 2.4.0） |
| 语言 | C#（LangVersion latest，Nullable + ImplicitUsings） |
| 运行时 | .NET 10（net10.0-windows10.0.19041.0，最小平台版本 10.0.17763.0） |
| 打包方式 | 未打包桌面应用（WindowsPackageType=None），**自包含部署**（WindowsAppSDKSelfContained） |
| 平台支持 | x86 / x64 / ARM64（默认 x64；csproj 内处理 AnyCPU → x64 映射） |
| 系统 API | System.Diagnostics.EventLog（事件日志）、System.Management（WMI / SMART / 硬件信息） |
| 转储解析 | 自研二进制解析（MDMP + PAGE/DU64），零第三方依赖 |
| AI 集成 | OpenAI 兼容 Chat Completions API（SSE 流式输出） |
| 构建 | .NET SDK 10+ / Visual Studio 2022（.slnx） |

### 依赖包

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
<PackageReference Include="System.Diagnostics.EventLog" Version="10.0.11" />
<PackageReference Include="System.Management" Version="10.0.11" />
```

---

## 目录结构

```
BlueScreenHelper/
├── BlueScreenHelper.slnx          # 解决方案（注意：构建请用 csproj，勿对 slnx）
├── README.md
└── BlueScreenHelper/
    ├── App.xaml(.cs)              # 应用入口，MainWindow 静态属性、导航注册
    ├── MainWindow.xaml(.cs)       # 主窗口 + 侧边导航（NavigateTo 切换页面）
    ├── app.manifest               # 权限声明
    ├── BlueScreenHelper.csproj    # 项目文件（AnyCPU→x64 映射）
    │
    ├── Models/                    # 数据模型
    │   ├── AppSettings.cs         #   应用设置（API Key 等）
    │   ├── BugCheckInfo.cs        #   BugCheck 条目（错误码/名称/类别/原因/方案）
    │   ├── DumpAnalysisResult.cs  #   转储解析结果（DumpModule/DumpAnalysisResult）
    │   └── ScanIssue.cs           #   扫描问题项 + DashboardCrashItem（崩溃记录）
    │
    ├── Services/                  # 业务逻辑（静态服务）
    │   ├── DumpParser.cs          #   .dmp 解析（MDMP + PAGE 内核/Triage 转储）
    │   ├── BugCheckDatabase.cs    #   蓝屏错误码知识库（数百个 BugCheck 代码）
    │   ├── SystemScanner.cs       #   系统扫描（驱动/服务/内存/磁盘/电池等）
    │   └── AIService.cs           #   AI 诊断（OpenAI 兼容 API，流式）
    │
    └── Views/                     # 页面（XAML + code-behind）
        ├── DashboardPage          #   首页：系统概览 + 崩溃记录
        ├── DumpPage               #   转储分析：选择 .dmp 解析显示结果
        ├── ScannerPage            #   系统扫描：逐项扫描并展示问题
        ├── AIPage                 #   AI 智能诊断对话
        └── SettingsPage           #   设置：API 配置
```

架构为轻量 MVVM 变体：`Views` 页面 + `Services` 无状态静态服务 + `Models` 数据类，页面通过 `App.MainWindow.NavigateTo` 切换，崩溃上下文经 `AppState.PendingAI` 传递至 AI 页。

---

## 构建

要求：**.NET SDK 10+**（或 VS 2022 17.14+）。

```powershell
# 构建（注意：直接构建项目文件，不要对解决方案构建）
dotnet build BlueScreenHelper/BlueScreenHelper.csproj

# 构建前若提示文件被占用（MSB3021），先关闭正在运行的应用：
Stop-Process -Name BlueScreenHelper
```

> 由于 WinUI 3 未打包应用要求明确平台，csproj 已内置 `AnyCPU → x64` 映射，因此**必须直接构建 csproj**，对 `BlueScreenHelper.slnx` 执行 `dotnet build` 会失败。

## 运行

```powershell
dotnet run --project BlueScreenHelper/BlueScreenHelper.csproj
```

或用 Visual Studio 打开 `BlueScreenHelper.slnx`，选择 x64 平台后启动。

### 发布自包含可执行文件

```powershell
dotnet publish BlueScreenHelper/BlueScreenHelper.csproj -c Release -r win-x64 --self-contained true
```

产物位于 `bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`，可拷贝到任意 x64 Windows 10 1809+ 机器直接运行。

---

## 使用说明

1. **转储分析**：打开「转储分析」页 → 选择 `C:\Windows\Minidump\*.dmp`（小转储）或 `C:\Windows\MEMORY.DMP`（内核转储，PAGE 格式）→ 自动解析并显示崩溃代码、参数注解、时间、系统版本与转储类型
2. **系统扫描**：点击「开始扫描」，逐项检查事件日志、磁盘健康（SMART）、磁盘空间与转储文件
3. **AI 诊断**：在「设置」中填写 API 地址（如 `https://api.deepseek.com/v1`）、密钥与模型，然后在「AI 诊断」页将崩溃报告提交给 AI 获取解决方案（支持本地 Ollama：`http://localhost:11434/v1`）

---

## 说明与限制

- 转储解析为**自研二进制解析**，不依赖 WinDbg / dbgeng.dll
- 内核转储（Triage 类型）不包含模块列表，此时故障驱动模块字段为空，属正常现象
- 扫描仅**读取**系统信息与事件日志，不做任何修改
- AI 密钥仅保存在本机 `%LOCALAPPDATA%\BlueScreenHelper\settings.json`，不上传；请勿将 settings.json 提交到版本库

---

## 许可证

本项目使用许可请参阅仓库 Licenses 标签页（暂未指定时默认为私有/保留所有权利）。