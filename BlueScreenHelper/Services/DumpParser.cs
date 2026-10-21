using System.Text;
using BlueScreenHelper.Models;

namespace BlueScreenHelper.Services;

public static class DumpParser
{
    private const uint MinidumpSignature = 0x504D444D;
    private const uint PageDumpSignature = 0x45474150;

    // MINIDUMP_STREAM_TYPE values are fixed by DbgHelp and do not depend on the
    // architecture of the process that produced the dump.
    private const uint StreamTypeSystemInfo = 0x0007;
    private const uint StreamTypeModuleList = 0x0004;
    private const uint StreamTypeBugCheck = 0x0505;
    private const uint StreamTypeException = 0x0006;
    private const uint StreamTypeMiscInfo = 0x000F;

    private const int MinidumpDirectorySize = 12;
    private const uint MinidumpModuleSize = 108;
    private const uint MiscInfoProcessIdFlag = 0x00000001;

    private readonly record struct StreamLocation(uint Rva, uint Size)
    {
        public bool IsPresent => Rva != 0 || Size != 0;
    }

    private static readonly HashSet<string> GpuDriverKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvlddmkm.sys", "nvddmkm.sys", "nvlddm.sys", "nvkflt.sys",
        "atikmpag.sys", "atikmdag.sys", "amdkmdag.sys", "amdkmdap.sys", "amdwddmg.sys",
        "igdkmd64.sys", "igdkmd32.sys", "igdgfx.sys", "igfxkmd.sys",
        "dxgkrnl.sys", "dxgmms1.sys", "dxgmms2.sys",
        "basicdisplay.sys", "basicrender.sys", "display.sys",
        "nvwgf2umx.dll", "igdumd64.dll"
    };

    private static readonly HashSet<uint> TdrBugChecks =
    [
        0x116, 0x117, 0x119, 0x10E, 0x113, 0x142
    ];

    private static readonly Dictionary<uint, string[]> BugCheckParamMeanings = new()
    {
        [0xD1] = new[] { "memory referenced（被引用的内存地址）", "IRQL（中断请求级别，0xFF=最高级别）", "value 0 = read, 1 = write, 8 = execute", "address which referenced memory（执行引用的指令地址）" },
        [0x0A] = new[] { "memory referenced（被引用的内存地址）", "IRQL（中断请求级别）", "value 0 = read, 1 = write, 8 = execute", "address which referenced memory（执行引用的指令地址）" },
        [0x1E] = new[] { "exception code（异常代码）", "faulting address（出错地址）", "exception parameter 0", "exception parameter 1" },
        [0x3B] = new[] { "exception code（异常代码）", "faulting address（出错地址）", "exception parameter 0", "exception parameter 1" },
        [0x133] = new[] { "watchdog type（0=单个 DPC/ISR 超时，1=累计运行超时）", "timeout data（含义取决于参数 1）", "triage block or DPC context（含义取决于参数 1）", "reserved" },
        [0x139] = new[] { "fast-fail type（安全检查失败类型）", "trap frame（陷阱帧地址）", "exception record（异常记录地址）", "reserved" },
        [0x116] = new[] { "TDR recovery context（内部恢复上下文）", "responsible driver module（疑似责任驱动模块）", "last failed operation status（失败操作的 NTSTATUS）", "context-dependent data（上下文数据）" },
        [0x119] = new[] { "video scheduler violation type（视频调度器违规类型）", "context-dependent data 1", "context-dependent data 2", "context-dependent data 3" },
        [0xC2] = new[] { "pool operation type（内存池操作类型）", "context-dependent data 1", "context-dependent data 2", "context-dependent data 3" },
        [0x1A] = new[] { "memory management subtype（内存管理错误子类型）", "context-dependent data 1", "context-dependent data 2", "context-dependent data 3" }
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
                               "1. 重新复制文件并确认传输完整（内核/完整转储通常为数百 MB 至数 GB）\n" +
                               "2. 使用“系统扫描”核对同一时间的蓝屏事件记录\n" +
                               "3. 在系统中重新启用转储后，等待下次崩溃生成新的转储文件";
                return result;
            }

            fs.Position = 4;
            _ = reader.ReadUInt32();
            var streamCount = reader.ReadUInt32();
            var dirRva = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var timeStamp = reader.ReadUInt32();

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
                result.Error = $"转储目录包含异常数量的流（{streamCount}），文件可能已损坏。";
                return result;
            }

            var directoryLength = (ulong)streamCount * MinidumpDirectorySize;
            if (!IsRangeValid(fs.Length, dirRva, directoryLength))
            {
                result.Error = "转储目录位置超出文件范围，文件可能不完整或已损坏。";
                return result;
            }

            bool is64Bit = true;
            StreamLocation systemInfo = default;
            StreamLocation moduleList = default;
            StreamLocation bugCheck = default;
            StreamLocation exception = default;
            StreamLocation miscInfo = default;

            fs.Position = dirRva;
            for (uint i = 0; i < streamCount; i++)
            {
                var type = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var rva = reader.ReadUInt32();
                var location = new StreamLocation(rva, size);
                switch (type)
                {
                    case StreamTypeSystemInfo when !systemInfo.IsPresent: systemInfo = location; break;
                    case StreamTypeModuleList when !moduleList.IsPresent: moduleList = location; break;
                    case StreamTypeBugCheck when !bugCheck.IsPresent: bugCheck = location; break;
                    case StreamTypeException when !exception.IsPresent: exception = location; break;
                    case StreamTypeMiscInfo when !miscInfo.IsPresent: miscInfo = location; break;
                }
            }

            if (TrySeekStream(fs, systemInfo, 20, "系统信息"))
            {
                var arch = reader.ReadUInt16();
                _ = reader.ReadUInt16();
                _ = reader.ReadUInt16();
                result.ProcessorCount = reader.ReadByte();
                _ = reader.ReadByte();
                var major = reader.ReadUInt32();
                var minor = reader.ReadUInt32();
                var build = reader.ReadUInt32();
                is64Bit = arch is 9 or 12;
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

            var bugCheckSize = (uint)(4 + (is64Bit ? 8 : 4) * 4);
            if (TrySeekStream(fs, bugCheck, bugCheckSize, "BugCheck 信息"))
            {
                result.BugCheckCode = reader.ReadUInt32();
                result.IsKernelDump = result.BugCheckCode != 0;
                for (int i = 0; i < 4; i++)
                {
                    result.BugCheckParameters[i] = is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
                }
            }

            if (TrySeekStream(fs, exception, 32, "异常信息"))
            {
                result.ThreadId = reader.ReadUInt32();
                fs.Position = exception.Rva + 8;
                var exCode = reader.ReadUInt32();
                result.ExceptionCode = exCode;
                // MINIDUMP_EXCEPTION uses fixed-width ULONG64 fields even for an
                // x86 target. ExceptionAddress is +16 within ExceptionRecord,
                // whose start is +8 within MINIDUMP_EXCEPTION_STREAM.
                fs.Position = exception.Rva + 24;
                result.ExceptionAddress = reader.ReadUInt64();
            }

            if (TrySeekStream(fs, miscInfo, 8, "杂项信息"))
            {
                var sizeOfInfo = reader.ReadUInt32();
                var flags = reader.ReadUInt32();
                if (sizeOfInfo > miscInfo.Size)
                {
                    throw new InvalidDataException("杂项信息流声明的结构大小超出流边界。");
                }
                if (sizeOfInfo >= 12 && miscInfo.Size >= 12 && (flags & MiscInfoProcessIdFlag) != 0)
                {
                    result.ProcessId = reader.ReadUInt32();
                }
            }

            var modules = new List<DumpModule>();
            if (TrySeekStream(fs, moduleList, 4, "模块列表"))
            {
                // NumberOfModules is always ULONG32; MINIDUMP_MODULE is always
                // 108 bytes, regardless of the target architecture.
                var count = reader.ReadUInt32();
                var availableEntries = (moduleList.Size - 4) / MinidumpModuleSize;
                if (count > availableEntries)
                {
                    throw new InvalidDataException("模块列表数量超出流边界，文件可能已损坏。");
                }
                var parseCount = Math.Min(count, 20000u);
                modules.Capacity = (int)parseCount;
                for (uint i = 0; i < parseCount; i++)
                {
                    var baseAddr = reader.ReadUInt64();
                    var size = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                    var nameRva = reader.ReadUInt32();
                    fs.Position += 52 + 8 + 8 + 8 + 8;

                    string name = "";
                    if (IsRangeValid(fs.Length, nameRva, 4))
                    {
                        var pos = fs.Position;
                        fs.Position = nameRva;
                        var len = reader.ReadUInt32();
                        if (len > 0 && len <= 32768 && len % 2 == 0 &&
                            IsRangeValid(fs.Length, (ulong)nameRva + 4, len))
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

            ResolveFaultingModule(result);

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

            result.IsValidMinidump = true;
        }
        catch (Exception ex)
        {
            result.IsValidMinidump = false;
            result.Error = $"解析失败：{ex.Message}";
        }

        return result;
    }

    private static bool TrySeekStream(Stream stream, StreamLocation location, uint minimumSize, string displayName)
    {
        if (!location.IsPresent)
        {
            return false;
        }
        if (location.Size < minimumSize)
        {
            throw new InvalidDataException($"{displayName}流长度不足（{location.Size} 字节）。");
        }
        if (!IsRangeValid(stream.Length, location.Rva, location.Size))
        {
            throw new InvalidDataException($"{displayName}流位置超出文件范围。");
        }
        stream.Position = location.Rva;
        return true;
    }

    private static bool IsRangeValid(long fileLength, ulong offset, ulong length)
    {
        if (fileLength < 0)
        {
            return false;
        }
        var size = (ulong)fileLength;
        return offset <= size && length <= size - offset;
    }

    private static void ParseKernelDump(BinaryReader reader, DumpAnalysisResult result)
    {
        var fs = reader.BaseStream;
        fs.Position = 0;
        var header = new byte[0x2000];
        var read = 0;
        while (read < header.Length)
        {
            var count = fs.Read(header, read, header.Length - read);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        if (read < 8)
        {
            result.Error = "内核转储文件过小，可能不完整。";
            return;
        }

        var secondarySignature = Encoding.ASCII.GetString(header, 4, 4);
        if (secondarySignature is not ("DU64" or "DUMP"))
        {
            result.Error = "PAGE 转储的辅助签名无效，文件可能已损坏。";
            return;
        }
        bool isX64 = secondarySignature == "DU64";
        var requiredHeaderSize = isX64 ? 0x2000 : 0x1000;
        if (read < requiredHeaderSize)
        {
            result.Error = $"内核转储头部不完整（需要 {requiredHeaderSize} 字节，实际 {read} 字节）。";
            return;
        }

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
        result.IsValidMinidump = true;
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

    private static void ResolveFaultingModule(DumpAnalysisResult result)
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
            if (TdrBugChecks.Contains(result.BugCheckCode))
            {
                foreach (var module in result.Modules)
                {
                    if (GpuDriverKeywords.Contains(module.Name))
                    {
                        result.FaultingModule = module.Name;
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
            if (m.BaseAddress > 0 && m.Size > 0 && address >= m.BaseAddress &&
                address - m.BaseAddress < m.Size && !string.IsNullOrWhiteSpace(m.Name))
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
        sb.AppendLine(r.BugCheckCode > 0 ? "# 蓝屏转储分析报告" : "# 应用程序崩溃转储分析报告");
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
        if (!r.IsValidMinidump)
        {
            sb.AppendLine("## 解析结果");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(r.Error) ? "无法解析该转储文件。" : r.Error);
            sb.AppendLine();
            return sb.ToString();
        }
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
            if (r.ThreadId is uint tid)
            {
                sb.AppendLine($"- **线程 ID**：{tid}");
            }
            sb.AppendLine();
        }
        else if (r.ExceptionCode is uint exceptionCode)
        {
            sb.AppendLine("## 异常信息");
            sb.AppendLine();
            sb.AppendLine($"- **异常代码**：0x{exceptionCode:X8}");
            if (r.ExceptionAddress is ulong address)
            {
                sb.AppendLine($"- **异常地址**：0x{address:X}");
            }
            if (!string.IsNullOrWhiteSpace(r.FaultingModule))
            {
                sb.AppendLine($"- **故障模块**：{r.FaultingModule}");
            }
            if (r.ProcessId is uint processId)
            {
                sb.AppendLine($"- **进程 ID**：{processId}");
            }
            if (r.ThreadId is uint threadId)
            {
                sb.AppendLine($"- **线程 ID**：{threadId}");
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
            foreach (var s in BugCheckDatabase.BuildPrioritizedSolutions(r.Knowledge, r.FaultingModule))
            {
                sb.AppendLine($"{n}. {s}");
                n++;
            }
            sb.AppendLine();
            if (r.Knowledge.SafetyNotes.Length > 0)
            {
                sb.AppendLine("## 执行前注意");
                sb.AppendLine();
                foreach (var note in r.Knowledge.SafetyNotes)
                {
                    sb.AppendLine($"- {note}");
                }
                sb.AppendLine();
            }
            if (r.Knowledge.VerificationSteps.Length > 0)
            {
                sb.AppendLine("## 如何验证是否修复");
                sb.AppendLine();
                n = 1;
                foreach (var step in r.Knowledge.VerificationSteps)
                {
                    sb.AppendLine($"{n}. {step}");
                    n++;
                }
                sb.AppendLine();
            }
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
        sb.AppendLine("[蓝屏转储文件分析数据]");
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
        if (r.ThreadId is uint tid)
        {
            sb.AppendLine($"- 线程ID: {tid}");
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
