using System.Text;
using BlueScreenHelper.Models;

namespace BlueScreenHelper.Services;

public static class DumpParser
{
    private const uint MinidumpSignature = 0x504D444D;
    private const uint PageDumpSignature = 0x45474150;

    private const uint StreamTypeSystemInfo = 0x0003;
    private const uint StreamTypeModuleList = 0x0004;
    private const uint StreamTypeBugCheck = 0x0505;
    private const uint StreamTypeException = 0x0006;
    private const uint StreamTypeMiscInfo = 0x0007;

    private static readonly string[] GpuDriverKeywords =
    {
        "nvlddmkm.sys", "nvddmkm.sys", "nvlddm.sys", "nvkflt.sys",
        "atikmpag.sys", "atikmdag.sys", "amdkmdag.sys", "amdkmdap.sys", "amdwddmg.sys",
        "igdkmd64.sys", "igdkmd32.sys", "igdgfx.sys", "igfxkmd.sys",
        "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys",
        "basicdisplay.sys", "basicrender.sys", "display.sys",
        "nvwgf2umx.dll", "igdumd64.dll"
    };

    private static readonly Dictionary<uint, string[]> BugCheckParamMeanings = new()
    {
        [0xD1] = new[] { "memory referenced（被引用的内存地址）", "IRQL（中断请求级别，0xFF=最高级别）", "value 0 = read, 1 = write, 8 = execute", "address which referenced memory（执行引用的指令地址）" },
        [0x0A] = new[] { "IRQL（中断请求级别）", "value 0 = read, 1 = write, 8 = execute", "address which referenced memory", "referencing module（引用内存的模块）" },
        [0x1E] = new[] { "exception code（异常代码）", "faulting address（出错地址）", "exception parameter 0", "exception parameter 1" },
        [0x3B] = new[] { "exception code（异常代码）", "faulting address（出错地址）", "exception parameter 0", "exception parameter 1" },
        [0x133] = new[] { "reserved", "reserved", "context（附加上下文）", "reserved" },
        [0x139] = new[] { "reserved", "reserved", "context（附加上下文）", "reserved" },
        [0x116] = new[] { "reset reason（重置原因）", "TDR flag", "context（附加上下文）", "reserved" },
        [0x119] = new[] { "context（附加上下文）", "video TDR context", "TDR flag", "reserved" },
        [0xC2] = new[] { "request type（请求类型）", "memory descriptor list", "memory descriptor list", "reserved" },
        [0x1A] = new[] { "memory management error code（错误代码）", "reserved", "reserved", "reserved" },
        [0xA1] = new[] { "disk signature（磁盘签名）", "disk dump version", "size of dump", "reserved" }
    };

    public static DumpAnalysisResult Parse(string path)
    {
        var result = new DumpAnalysisResult
        {
            FilePath = path
        };

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                result.Error = "文件不存在或无法访问。";
                return result;
            }
            result.FileSize = fi.Length;
            result.FileModified = fi.LastWriteTime;

            using var fs = File.OpenRead(path);
            if (fs.Length < 32)
            {
                result.Error = "文件过小，不是有效的转储文件。";
                return result;
            }

            using var reader = new BinaryReader(fs);

            var signature = reader.ReadUInt32();
            if (signature == PageDumpSignature)
            {
                ParseKernelDump(reader, result);
                return result;
            }
            if (signature != MinidumpSignature)
            {
                result.Error = "该文件既不是标准 MDMP 小转储，也不是 Windows 内核转储（PAGE 格式）。\n" +
                               "可能是文件损坏、未完成的转储，或第三方工具导出的数据。可尝试：\n" +
                               "1. 确认文件完整（内核/完整转储通常为数百 MB 至数 GB）\n" +
                               "2. 使用 WinDbg 打开分析：`windbg -z 文件路径` 并执行 `!analyze -v`\n" +
                               "3. 使用“AI 智能诊断”提交该文件信息进行辅助分析";
                return result;
            }

            fs.Position = 4;
            _ = reader.ReadUInt32();
            var streamCount = reader.ReadUInt32();
            var dirRva = reader.ReadInt32();
            _ = reader.ReadUInt32();
            var timeStamp = reader.ReadUInt32();

            result.IsValidMinidump = true;
            if (timeStamp != 0)
            {
                try
                {
                    result.DumpTime = DateTimeOffset.FromUnixTimeSeconds(timeStamp).ToLocalTime().DateTime;
                }
                catch
                {
                }
            }

            if (streamCount > 4096)
            {
                streamCount = 4096;
            }

            bool isX64 = true;
            uint systemInfoRva = 0, moduleListRva = 0, bugCheckRva = 0, exceptionRva = 0, miscInfoRva = 0;

            fs.Position = dirRva;
            for (uint i = 0; i < streamCount; i++)
            {
                if (fs.Position + 24 > fs.Length)
                {
                    break;
                }
                var type = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var rva = reader.ReadUInt32();
                switch (type)
                {
                    case StreamTypeSystemInfo: systemInfoRva = rva; break;
                    case StreamTypeModuleList: moduleListRva = rva; break;
                    case StreamTypeBugCheck: bugCheckRva = rva; break;
                    case StreamTypeException: exceptionRva = rva; break;
                    case StreamTypeMiscInfo: miscInfoRva = rva; break;
                }
            }

            if (systemInfoRva > 0)
            {
                fs.Position = systemInfoRva;
                var arch = reader.ReadUInt16();
                _ = reader.ReadUInt16();
                _ = reader.ReadUInt16();
                result.ProcessorCount = reader.ReadByte();
                _ = reader.ReadByte();
                var major = reader.ReadUInt32();
                var minor = reader.ReadUInt32();
                var build = reader.ReadUInt32();
                isX64 = arch == 9;
                result.ProcessorArch = arch switch
                {
                    0 => "x86 (32位)",
                    9 => "x64 (64位)",
                    5 => "ARM",
                    12 => "ARM64",
                    _ => $"未知 ({arch})"
                };
                result.OSVersion = BuildOsVersion(major, minor, build);
            }

            if (bugCheckRva > 0)
            {
                fs.Position = bugCheckRva;
                result.BugCheckCode = reader.ReadUInt32();
                result.IsKernelDump = result.BugCheckCode != 0;
                fs.Position = bugCheckRva + 4;
                for (int i = 0; i < 4; i++)
                {
                    result.BugCheckParameters[i] = isX64 ? reader.ReadUInt64() : reader.ReadUInt32();
                }
            }

            if (exceptionRva > 0)
            {
                fs.Position = exceptionRva;
                result.ProcessId = reader.ReadUInt32();
                fs.Position = exceptionRva + 8;
                var exCode = reader.ReadUInt32();
                result.ExceptionCode = exCode;
                if (isX64)
                {
                    fs.Position = exceptionRva + 8 + 24;
                    result.ExceptionAddress = reader.ReadUInt64();
                }
                else
                {
                    fs.Position = exceptionRva + 8 + 16;
                    result.ExceptionAddress = reader.ReadUInt32();
                }
            }

            if (miscInfoRva > 0)
            {
                fs.Position = miscInfoRva;
                var sizeOfInfo = reader.ReadUInt32();
                if (sizeOfInfo >= 8 && result.ProcessId == null)
                {
                    result.ProcessId = reader.ReadUInt32();
                }
            }

            var modules = new List<DumpModule>();
            if (moduleListRva > 0)
            {
                fs.Position = moduleListRva;
                var count = isX64 ? reader.ReadUInt64() : reader.ReadUInt32();
                if (count > 20000)
                {
                    count = 20000;
                }
                for (ulong i = 0; i < count; i++)
                {
                    var baseAddr = reader.ReadUInt64();
                    var size = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    var nameRva = reader.ReadUInt32();
                    fs.Position += 52 + 8 + 8 + 8 + 8;

                    string name = "";
                    if (nameRva > 0 && nameRva + 4 <= fs.Length)
                    {
                        var pos = fs.Position;
                        fs.Position = nameRva;
                        var len = reader.ReadUInt32();
                        if (len > 0 && len <= 4096 && nameRva + 4 + len <= fs.Length)
                        {
                            var bytes = reader.ReadBytes((int)len);
                            name = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                        }
                        fs.Position = pos;
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        name = Path.GetFileName(name);
                    }
                    modules.Add(new DumpModule { BaseAddress = baseAddr, Size = size, Name = name });
                }
            }
            result.Modules = modules;

            ResolveFaultingModule(result, isX64);

            if (result.IsKernelDump && result.BugCheckCode > 0)
            {
                result.Knowledge = BugCheckDatabase.Get(result.BugCheckCode);
            }
            else if (result.ExceptionCode is uint ex)
            {
                var entry = new BugCheckEntry
                {
                    Code = 0,
                    Name = $"异常代码 0x{ex:X8}",
                    Category = "应用程序异常",
                    Description = "这是用户态应用程序崩溃转储，异常代码对应的常见含义：\n" +
                                  $"0x{(ex & 0xFFFF):X4} 为具体异常码。若为 0xC0000005 则属于访问冲突（内存越界）。",
                    Causes = new[] { "应用程序自身缺陷", "依赖组件版本不兼容", "内存损坏", "资源耗尽" },
                    Solutions = new[] { "查看崩溃进程与异常代码，更新该程序或依赖组件", "检查内存健康", "重装相关软件" }
                };
                result.Knowledge = entry;
                result.BugCheckName = entry.Name;
            }

            if (result.BugCheckCode > 0 && result.Knowledge == null)
            {
                result.BugCheckName = "未知错误代码（知识库未收录）";
            }
            else if (result.BugCheckCode > 0 && result.Knowledge != null)
            {
                result.BugCheckName = result.Knowledge.Name;
            }
        }
        catch (Exception ex)
        {
            result.Error = $"解析失败：{ex.Message}";
        }

        return result;
    }

    private static void ParseKernelDump(BinaryReader reader, DumpAnalysisResult result)
    {
        var fs = reader.BaseStream;
        fs.Position = 0;
        var header = new byte[0x2000];
        int read = fs.Read(header, 0, header.Length);
        if (read < 0x100)
        {
            result.Error = "内核转储文件过小，可能不完整。";
            return;
        }

        bool isX64 = Encoding.ASCII.GetString(header, 4, 4) == "DU64";

        var code = BitConverter.ToUInt32(header, isX64 ? 0x38 : 0x28);
        int paramOffset = isX64 ? 0x40 : 0x2C;
        int paramSize = isX64 ? 8 : 4;
        for (int i = 0; i < 4; i++)
        {
            int off = paramOffset + i * paramSize;
            result.BugCheckParameters[i] = isX64
                ? BitConverter.ToUInt64(header, off)
                : BitConverter.ToUInt32(header, off);
        }

        var machineType = BitConverter.ToUInt32(header, isX64 ? 0x30 : 0x20);
        result.ProcessorArch = machineType switch
        {
            0x8664 => "x64 (64位)",
            0x14C => "x86 (32位)",
            0xAA64 => "ARM64",
            0x1C4 => "ARM",
            _ => $"未知 (0x{machineType:X})"
        };

        var major = BitConverter.ToUInt32(header, 0x08);
        var minor = BitConverter.ToUInt32(header, 0x0C);
        result.OSVersion = minor >= 22000 ? $"Windows 11 (内部版本 {minor})"
                         : minor >= 10240 ? $"Windows 10 (内部版本 {minor})"
                         : BuildOsVersion(major, minor, 0);

        if (isX64)
        {
            var dumpType = BitConverter.ToUInt32(header, 0xF98);
            result.KernelDumpTypeText = dumpType switch
            {
                1 => "完整内存转储",
                2 => "内核内存转储",
                3 => "仅头部转储",
                4 => "Triage 转储",
                5 => "完整内存转储",
                6 => "内核内存转储",
                7 => "自动内存转储",
                _ => $"内核转储 (类型 {dumpType})"
            };

            result.DumpComment = ExtractAscii(header, 0xFB0, 128);
            if (result.DumpComment.Contains("PAGE", StringComparison.OrdinalIgnoreCase) ||
                result.DumpComment.Length < 4)
            {
                result.DumpComment = "";
            }

            var ft = BitConverter.ToInt64(header, 0xFA8);
            if (ft > 0)
            {
                try
                {
                    result.DumpTime = DateTime.FromFileTimeUtc(ft).ToLocalTime();
                }
                catch
                {
                }
            }
        }
        else
        {
            result.KernelDumpTypeText = "内核转储 (32位)";
        }

        result.IsValidMinidump = true;
        result.IsKernelDump = true;
        result.DumpTime ??= result.FileModified;

        if (code == 0)
        {
            result.Error = "未能从转储头部读取到有效的 BugCheck 代码，文件可能已损坏。";
            return;
        }

        result.BugCheckCode = code;
        result.Knowledge = BugCheckDatabase.Get(code);
        result.BugCheckName = result.Knowledge?.Name ?? "未知错误代码（知识库未收录）";
    }

    public static string[]? GetBugCheckParamMeanings(uint code)
    {
        return BugCheckParamMeanings.TryGetValue(code, out var meanings) ? meanings : null;
    }

    private static string ExtractAscii(byte[] buf, int offset, int length)
    {
        int end = Math.Min(offset + length, buf.Length);
        var sb = new StringBuilder();
        for (int i = offset; i < end; i++)
        {
            byte b = buf[i];
            if (b == 0)
            {
                break;
            }
            if (b is >= 0x20 and <= 0x7E)
            {
                sb.Append((char)b);
            }
            else if (b == '\r' || b == '\n' || b == '\t')
            {
                sb.Append(b == '\t' ? ' ' : (char)b);
            }
            else
            {
                break;
            }
        }
        return sb.ToString().Trim();
    }

    private static void ResolveFaultingModule(DumpAnalysisResult result, bool isX64)
    {
        if (result.Modules.Count == 0)
        {
            return;
        }

        if (result.ExceptionAddress is ulong exAddr && exAddr > 0)
        {
            var byEx = FindModule(result.Modules, exAddr);
            if (byEx != null)
            {
                result.FaultingModule = byEx;
                return;
            }
        }

        foreach (var p in result.BugCheckParameters)
        {
            if (p == 0)
            {
                continue;
            }
            var m = FindModule(result.Modules, p);
            if (m != null)
            {
                result.FaultingModule = m;
                break;
            }
        }

        if (result.FaultingModule == null && result.IsKernelDump)
        {
            var tdrFamily = new HashSet<uint> { 0x116, 0x117, 0x119, 0x10E, 0x113, 0x142 };
            if (tdrFamily.Contains(result.BugCheckCode))
            {
                foreach (var keyword in GpuDriverKeywords)
                {
                    var gpu = result.Modules.FirstOrDefault(m =>
                        m.Name.Equals(keyword, StringComparison.OrdinalIgnoreCase));
                    if (gpu != null)
                    {
                        result.FaultingModule = gpu.Name;
                        break;
                    }
                }
            }
        }
    }

    private static string? FindModule(List<DumpModule> modules, ulong address)
    {
        foreach (var m in modules)
        {
            if (m.BaseAddress > 0 && m.Size > 0 &&
                address >= m.BaseAddress && address < m.BaseAddress + m.Size)
            {
                return m.Name;
            }
        }
        return null;
    }

    private static string BuildOsVersion(uint major, uint minor, uint build)
    {
        var osName = major == 10 && build >= 22000 ? "Windows 11" :
                     major == 10 ? "Windows 10" :
                     major == 6 && minor == 3 ? "Windows 8.1" :
                     major == 6 && minor == 2 ? "Windows 8" :
                     major == 6 && minor == 1 ? "Windows 7" : $"Windows {major}.{minor}";
        return $"{osName} (版本 {major}.{minor}.{build})";
    }

    public static string BuildReportMarkdown(DumpAnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 蓝屏转储分析报告");
        sb.AppendLine();
        sb.AppendLine($"- **文件**：{r.FilePath}");
        sb.AppendLine($"- **文件大小**：{FormatSize(r.FileSize)}");
        if (r.DumpTime is DateTime t)
        {
            sb.AppendLine($"- **崩溃时间**：{t:yyyy-MM-dd HH:mm:ss}");
        }
        if (!string.IsNullOrEmpty(r.OSVersion))
        {
            sb.AppendLine($"- **系统版本**：{r.OSVersion}");
        }
        sb.AppendLine($"- **转储类型**：{r.DumpTypeText}");
        sb.AppendLine();
        if (r.BugCheckCode > 0)
        {
            sb.AppendLine($"## 崩溃信息");
            sb.AppendLine();
            sb.AppendLine($"- **错误代码**：{r.BugCheckHex}");
            sb.AppendLine($"- **错误名称**：{r.BugCheckName}");
            sb.AppendLine($"- **类别**：{r.Knowledge?.Category ?? "未知"}");
            sb.AppendLine("- **参数**：");
            for (int i = 0; i < r.BugCheckParameters.Length; i++)
            {
                sb.AppendLine($"  - 参数 {i + 1}：0x{r.BugCheckParameters[i]:X}");
            }
            if (!string.IsNullOrEmpty(r.FaultingModule))
            {
                sb.AppendLine($"- **故障模块**：{r.FaultingModule}");
            }
            if (r.ProcessId is uint pid)
            {
                sb.AppendLine($"- **进程 ID**：{pid}");
            }
            sb.AppendLine();
        }
        if (r.Knowledge != null)
        {
            sb.AppendLine($"## 问题描述");
            sb.AppendLine();
            sb.AppendLine(r.Knowledge.Description);
            sb.AppendLine();
            sb.AppendLine("## 常见原因");
            sb.AppendLine();
            foreach (var c in r.Knowledge.Causes)
            {
                sb.AppendLine($"- {c}");
            }
            sb.AppendLine();
            sb.AppendLine("## 解决方案");
            sb.AppendLine();
            int n = 1;
            foreach (var s in r.Knowledge.Solutions)
            {
                sb.AppendLine($"{n}. {s}");
                n++;
            }
            sb.AppendLine();
            if (r.Knowledge.RelatedDrivers.Length > 0)
            {
                sb.AppendLine("## 相关驱动");
                sb.AppendLine();
                foreach (var d in r.Knowledge.RelatedDrivers)
                {
                    sb.AppendLine($"- {d}");
                }
                sb.AppendLine();
            }
        }
        if (r.Modules.Count > 0)
        {
            sb.AppendLine("## 已加载模块（前 30 个）");
            sb.AppendLine();
            foreach (var m in r.Modules.Take(30))
            {
                sb.AppendLine($"- {m.Name} (基址 0x{m.BaseAddress:X}, 大小 0x{m.Size:X})");
            }
        }
        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }
        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    public static string BuildAnalysisText(DumpAnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【蓝屏转储文件分析数据】");
        sb.AppendLine($"- 文件: {r.FilePath}");
        sb.AppendLine($"- 大小: {FormatSize(r.FileSize)}");
        if (r.DumpTime is DateTime t)
        {
            sb.AppendLine($"- 崩溃时间: {t:yyyy-MM-dd HH:mm:ss}");
        }
        if (!string.IsNullOrEmpty(r.OSVersion))
        {
            sb.AppendLine($"- 操作系统: {r.OSVersion}");
        }
        sb.AppendLine($"- 转储类型: {r.DumpTypeText}");
        if (!string.IsNullOrEmpty(r.DumpComment))
        {
            sb.AppendLine($"- 转储注释: {r.DumpComment}");
        }
        if (r.BugCheckCode > 0)
        {
            sb.AppendLine($"- 错误代码: {r.BugCheckHex} ({r.BugCheckName})");
            sb.AppendLine("- 参数:");
            for (int i = 0; i < r.BugCheckParameters.Length; i++)
            {
                sb.AppendLine($"  - 参数{i + 1}: 0x{r.BugCheckParameters[i]:X}");
            }
        }
        if (!string.IsNullOrEmpty(r.FaultingModule))
        {
            sb.AppendLine($"- 疑似故障模块: {r.FaultingModule}");
        }
        if (r.ExceptionCode is uint ec)
        {
            sb.AppendLine($"- 异常代码: 0x{ec:X8}");
        }
        if (r.ProcessId is uint pid)
        {
            sb.AppendLine($"- 进程ID: {pid}");
        }
        if (r.Knowledge != null)
        {
            sb.AppendLine($"- 知识库描述: {r.Knowledge.Description}");
        }
        sb.AppendLine("- 已加载模块(前25个):");
        foreach (var m in r.Modules.Take(25))
        {
            sb.AppendLine($"  - {m.Name} 基址=0x{m.BaseAddress:X} 大小=0x{m.Size:X}");
        }
        return sb.ToString();
    }
}