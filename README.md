# BlueScreenHelper

WinUI 3 蓝屏诊断助手：.dmp 转储解析、系统问题扫描、AI 诊断建议。

## 功能

- **首页**：系统关键信息概览、最近崩溃记录
- **转储分析**：解析 Minidump 文件（.dmp），提取崩溃代码、异常信息、模块列表，生成分析报告
- **系统扫描**：检查关键事件日志（BugCheck、WHEA、磁盘错误、内存诊断等）、SMART 健康状态、磁盘空间、崩溃转储文件、待重启状态
- **AI 诊断**：将分析结果发送给 OpenAI 兼容 API（支持 OpenAI / DeepSeek / 智谱 / Moonshot / 通义 / SiliconFlow / Ollama 等），生成问题定位与解决方案（流式输出）
- **设置**：配置 API 地址、密钥、模型、温度与系统提示词（存储于 `%LOCALAPPDATA%\BlueScreenHelper\settings.json`）

## 构建

要求：.NET SDK 10+。

```powershell
dotnet build BlueScreenHelper/BlueScreenHelper.csproj
```

注意：由于 WinUI 3 未打包应用要求明确平台，请**直接构建项目文件**（csproj 内已处理 AnyCPU → x64 映射），不要对解决方案执行 `dotnet build`。

## 运行

```powershell
dotnet run --project BlueScreenHelper/BlueScreenHelper.csproj
```

或用 Visual Studio 打开 `BlueScreenHelper.slnx`，选择 x64 平台后运行。

## 说明

- 转储解析为自研 MDMP 二进制解析，不依赖 WinDbg；完整内存转储（非 MDMP）暂不支持，会给出提示
- 扫描仅读取系统信息与事件日志，不做任何修改
