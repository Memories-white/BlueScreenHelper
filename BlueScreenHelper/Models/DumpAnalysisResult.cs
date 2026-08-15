namespace BlueScreenHelper.Models;

public sealed class DumpModule
{
    public ulong BaseAddress { get; set; }
    public ulong Size { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DumpAnalysisResult
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime FileModified { get; set; }

    public bool IsValidMinidump { get; set; }
    public string Error { get; set; } = "";

    public bool IsKernelDump { get; set; }
    public string KernelDumpTypeText { get; set; } = "";
    public string DumpTypeText => !string.IsNullOrEmpty(KernelDumpTypeText)
        ? KernelDumpTypeText
        : (IsKernelDump ? "内核转储 (蓝屏崩溃)" : "用户态转储 (应用程序崩溃)");

    public string DumpComment { get; set; } = "";

    public uint BugCheckCode { get; set; }
    public string BugCheckHex => $"0x{BugCheckCode:X8}";
    public string BugCheckName { get; set; } = "";
    public ulong[] BugCheckParameters { get; set; } = new ulong[4];

    public DateTime? DumpTime { get; set; }
    public string? OSVersion { get; set; }
    public string ProcessorArch { get; set; } = "";
    public int ProcessorCount { get; set; }

    public uint? ExceptionCode { get; set; }
    public ulong? ExceptionAddress { get; set; }
    public uint? ProcessId { get; set; }

    public string? FaultingModule { get; set; }
    public List<DumpModule> Modules { get; set; } = new();
    public BugCheckEntry? Knowledge { get; set; }
}