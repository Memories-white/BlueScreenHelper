using System.Text;
using BlueScreenHelper.Services;
using Xunit;

namespace BlueScreenHelper.Tests;

public sealed class DumpParserTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "BlueScreenHelper.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parse_MissingFile_ReturnsUsefulError()
    {
        var path = Path.Combine(_tempDirectory, "missing.dmp");

        var result = DumpParser.Parse(path);

        Assert.False(result.IsValidMinidump);
        Assert.Contains("文件不存在", result.Error);
        Assert.Equal(path, result.FilePath);
    }

    [Fact]
    public void Parse_FileSmallerThanHeader_IsRejected()
    {
        var path = WriteFile("too-small.dmp", new byte[31]);

        var result = DumpParser.Parse(path);

        Assert.False(result.IsValidMinidump);
        Assert.Contains("文件过小", result.Error);
    }

    [Fact]
    public void Parse_UnknownSignature_IsRejected()
    {
        var bytes = new byte[32];
        Encoding.ASCII.GetBytes("NOPE").CopyTo(bytes, 0);
        var path = WriteFile("unknown.dmp", bytes);

        var result = DumpParser.Parse(path);

        Assert.False(result.IsValidMinidump);
        Assert.Contains("不是标准 MDMP", result.Error);
    }

    [Fact]
    public void Parse_StandardX64SystemInfoAndModuleList_ReadsDocumentedLayout()
    {
        const ulong moduleBase = 0x00007FF600000000;
        const uint moduleSize = 0x00123000;
        const ulong exceptionAddress = moduleBase + 0x1234;
        var path = WriteFile("standard-x64.dmp", CreateStandardX64Minidump(
            moduleBase,
            moduleSize,
            @"C:\Windows\System32\drivers\example.sys",
            exceptionAddress));

        var result = DumpParser.Parse(path);

        Assert.True(result.IsValidMinidump, result.Error);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal("x64 (64位)", result.ProcessorArch);
        Assert.Equal(16, result.ProcessorCount);
        Assert.Equal("Windows 11 (版本 10.0.22631)", result.OSVersion);

        var module = Assert.Single(result.Modules);
        Assert.Equal("example.sys", module.Name);
        Assert.Equal(moduleBase, module.BaseAddress);
        Assert.Equal(moduleSize, module.Size);
        Assert.Equal(0xC0000005u, result.ExceptionCode);
        Assert.Equal(exceptionAddress, result.ExceptionAddress);
        Assert.Equal(77u, result.ThreadId);
        Assert.Equal(4242u, result.ProcessId);
        Assert.Equal("example.sys", result.FaultingModule);
    }

    [Fact]
    public void Parse_DirectoryOutsideFile_IsRejectedWithoutMarkingDumpValid()
    {
        var bytes = new byte[32];
        using (var stream = new MemoryStream(bytes, writable: true))
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0x504D444Du);
            writer.Write(0x0000A793u);
            writer.Write(1u);
            writer.Write(0xFFFF_FFF0u);
        }
        var path = WriteFile("bad-directory.dmp", bytes);

        var result = DumpParser.Parse(path);

        Assert.False(result.IsValidMinidump);
        Assert.Contains("目录位置超出文件范围", result.Error);
    }

    [Fact]
    public void GetBugCheckParamMeanings_IrqlNotLessOrEqual_UsesDocumentedOrder()
    {
        var meanings = DumpParser.GetBugCheckParamMeanings(0x0A);

        Assert.NotNull(meanings);
        Assert.Contains("内存地址", meanings![0]);
        Assert.Contains("IRQL", meanings[1]);
        Assert.Contains("read", meanings[2]);
        Assert.Contains("指令地址", meanings[3]);
        Assert.Null(DumpParser.GetBugCheckParamMeanings(0xA1));
    }

    [Fact]
    public void BuildReportMarkdown_UserModeDump_IncludesExceptionEvidence()
    {
        var result = new BlueScreenHelper.Models.DumpAnalysisResult
        {
            IsValidMinidump = true,
            FilePath = @"C:\\Crash Dumps\\sample.dmp",
            FileSize = 4096,
            ExceptionCode = 0xC0000005,
            ExceptionAddress = 0x00007FF612341234,
            FaultingModule = "example.dll",
            ProcessId = 4242,
            ThreadId = 77
        };

        var report = DumpParser.BuildReportMarkdown(result);

        Assert.StartsWith("# 应用程序崩溃转储分析报告", report);
        Assert.Contains("0xC0000005", report);
        Assert.Contains("0x7FF612341234", report);
        Assert.Contains("example.dll", report);
        Assert.Contains("4242", report);
        Assert.Contains("77", report);
    }

    [Fact]
    public void BuildReportMarkdown_InvalidDump_IncludesParseErrorAndStops()
    {
        var result = new BlueScreenHelper.Models.DumpAnalysisResult
        {
            FilePath = "broken.dmp",
            Error = "文件结构已损坏。"
        };

        var report = DumpParser.BuildReportMarkdown(result);

        Assert.Contains("## 解析结果", report);
        Assert.Contains("文件结构已损坏", report);
        Assert.DoesNotContain("## 已加载模块", report);
    }

    [Fact]
    public void BuildReportMarkdown_KnownBugCheck_IncludesSafetyAndVerification()
    {
        var result = new BlueScreenHelper.Models.DumpAnalysisResult
        {
            IsValidMinidump = true,
            FilePath = "storage-crash.dmp",
            BugCheckCode = 0x24,
            BugCheckName = "NTFS_FILE_SYSTEM",
            FaultingModule = "vendorstorage.sys",
            Knowledge = BugCheckDatabase.Get(0x24)
        };

        var report = DumpParser.BuildReportMarkdown(result);

        Assert.Contains("转储直接指向 vendorstorage.sys", report);
        Assert.Contains("## 执行前注意", report);
        Assert.Contains("## 如何验证是否修复", report);
        Assert.Contains("备份", report);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] CreateStandardX64Minidump(
        ulong moduleBase,
        uint moduleSize,
        string modulePath,
        ulong exceptionAddress)
    {
        const int headerRva = 0;
        const int directoryRva = 32;
        const int systemInfoRva = 80;
        const int moduleListRva = 136;
        const int moduleRva = moduleListRva + sizeof(uint);
        const int moduleSizeInDump = 108;
        const int moduleNameRva = moduleRva + moduleSizeInDump;

        var moduleName = Encoding.Unicode.GetBytes(modulePath);
        var exceptionRva = Align8(moduleNameRva + sizeof(uint) + moduleName.Length);
        var miscInfoRva = exceptionRva + 168;
        var bytes = new byte[miscInfoRva + 24];
        using var stream = new MemoryStream(bytes, writable: true);
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        // MINIDUMP_HEADER (32 bytes).
        stream.Position = headerRva;
        writer.Write(0x504D444Du); // Signature: "MDMP"
        writer.Write(0x0000A793u); // MINIDUMP_VERSION
        writer.Write(4u);          // NumberOfStreams
        writer.Write(directoryRva);
        writer.Write(0u);          // CheckSum
        writer.Write(1_700_000_000u);
        writer.Write(0UL);         // Flags

        // MINIDUMP_DIRECTORY entries are 12 bytes each.
        stream.Position = directoryRva;
        WriteDirectory(writer, streamType: 7, dataSize: 56, rva: systemInfoRva);
        WriteDirectory(writer, streamType: 4, dataSize: sizeof(uint) + moduleSizeInDump, rva: moduleListRva);
        WriteDirectory(writer, streamType: 6, dataSize: 168, rva: exceptionRva);
        WriteDirectory(writer, streamType: 15, dataSize: 24, rva: miscInfoRva);

        // MINIDUMP_SYSTEM_INFO (56 bytes). Only documented scalar fields are
        // populated; the CPU-specific union remains zero-filled.
        stream.Position = systemInfoRva;
        writer.Write((ushort)9);       // PROCESSOR_ARCHITECTURE_AMD64
        writer.Write((ushort)6);       // ProcessorLevel
        writer.Write((ushort)0xA00);   // ProcessorRevision
        writer.Write((byte)16);        // NumberOfProcessors
        writer.Write((byte)1);         // VER_NT_WORKSTATION
        writer.Write(10u);             // MajorVersion
        writer.Write(0u);              // MinorVersion
        writer.Write(22631u);          // BuildNumber
        writer.Write(2u);              // VER_PLATFORM_WIN32_NT
        writer.Write(0u);              // CSDVersionRva
        writer.Write((ushort)0);       // SuiteMask
        writer.Write((ushort)0);       // Reserved2

        // MINIDUMP_MODULE_LIST starts with a 32-bit count on every architecture.
        stream.Position = moduleListRva;
        writer.Write(1u);
        stream.Position = moduleRva;
        writer.Write(moduleBase);
        writer.Write(moduleSize);
        writer.Write(0u); // CheckSum
        writer.Write(0u); // TimeDateStamp
        writer.Write(moduleNameRva);
        stream.Position = moduleRva + moduleSizeInDump;

        // MINIDUMP_STRING length is a byte count and excludes a terminator.
        writer.Write((uint)moduleName.Length);
        writer.Write(moduleName);

        // MINIDUMP_EXCEPTION_STREAM. ExceptionAddress is 24 bytes from the
        // start of the stream (16 bytes into MINIDUMP_EXCEPTION).
        stream.Position = exceptionRva;
        writer.Write(77u);         // ThreadId (not a process id)
        writer.Write(0u);          // Alignment
        writer.Write(0xC0000005u); // ExceptionCode: access violation
        writer.Write(0u);          // ExceptionFlags
        writer.Write(0UL);         // Nested ExceptionRecord
        writer.Write(exceptionAddress);
        writer.Write(0u);          // NumberParameters
        writer.Write(0u);          // Alignment
        stream.Position = exceptionRva + 160;
        writer.Write(0u);          // ThreadContext.DataSize
        writer.Write(0u);          // ThreadContext.Rva

        // MINIDUMP_MISC_INFO. ProcessId is valid only when Flags1 contains
        // MINIDUMP_MISC1_PROCESS_ID, and lives at offset 8.
        stream.Position = miscInfoRva;
        writer.Write(24u); // SizeOfInfo
        writer.Write(1u);  // Flags1: MINIDUMP_MISC1_PROCESS_ID
        writer.Write(4242u);
        return bytes;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private static void WriteDirectory(BinaryWriter writer, uint streamType, uint dataSize, int rva)
    {
        writer.Write(streamType);
        writer.Write(dataSize);
        writer.Write(rva);
    }
}
