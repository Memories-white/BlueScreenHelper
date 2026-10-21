using BlueScreenHelper.Models;
using BlueScreenHelper.Services;
using Xunit;

namespace BlueScreenHelper.Tests;

public sealed class BugCheckDatabaseTests
{
    [Fact]
    public void AllEntries_HaveActionableStructuredGuidance()
    {
        Assert.True(BugCheckDatabase.All.Count >= 60,
            $"Expected broad stop-code coverage, found {BugCheckDatabase.All.Count} entries.");

        foreach (var entry in BugCheckDatabase.All)
        {
            Assert.NotEqual(0u, entry.Code);
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Category));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.True(entry.Causes.Length >= 2, $"{entry.CodeHex} has too few causes.");
            Assert.True(entry.Solutions.Length >= 2, $"{entry.CodeHex} has too few solutions.");
            Assert.True(entry.VerificationSteps.Length >= 2, $"{entry.CodeHex} has too few verification steps.");
            Assert.True(entry.SafetyNotes.Length >= 2, $"{entry.CodeHex} has too few safety notes.");
            Assert.All(entry.Solutions, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.All(entry.VerificationSteps, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.All(entry.SafetyNotes, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
    }

    [Theory]
    [InlineData(0x00000018u, "REFERENCE_BY_POINTER")]
    [InlineData(0x00000019u, "BAD_POOL_HEADER")]
    [InlineData(0x0000004Eu, "PFN_LIST_CORRUPT")]
    [InlineData(0x00000074u, "BAD_SYSTEM_CONFIG_INFO")]
    [InlineData(0x000000A5u, "ACPI_BIOS_ERROR")]
    [InlineData(0x000000EDu, "UNMOUNTABLE_BOOT_VOLUME")]
    [InlineData(0x0000012Bu, "FAULTY_HARDWARE_CORRUPTED_PAGE")]
    public void Get_CommonModernCodes_ReturnsExpectedEntry(uint code, string expectedName)
    {
        var entry = BugCheckDatabase.Get(code);

        Assert.NotNull(entry);
        Assert.Equal(expectedName, entry.Name);
    }

    [Fact]
    public void BuildPrioritizedSolutions_PutsThirdPartyModuleFirst()
    {
        var entry = Assert.IsType<BugCheckEntry>(BugCheckDatabase.Get(0x116));

        var steps = BugCheckDatabase.BuildPrioritizedSolutions(entry, @"C:\Windows\System32\drivers\nvlddmkm.sys");

        Assert.Contains("nvlddmkm.sys", steps[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("回滚", steps[0]);
        Assert.True(steps.Count > entry.Solutions.Length);
    }

    [Fact]
    public void BuildPrioritizedSolutions_DoesNotBlameWindowsKernelModule()
    {
        var entry = Assert.IsType<BugCheckEntry>(BugCheckDatabase.Get(0x0A));

        var steps = BugCheckDatabase.BuildPrioritizedSolutions(entry, "ntoskrnl.exe");

        Assert.Equal(entry.Solutions, steps);
        Assert.DoesNotContain(steps, step => step.Contains("转储直接指向", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Wdf01000.sys")]
    [InlineData("storport.sys")]
    [InlineData("tcpip.sys")]
    public void BuildPrioritizedSolutions_DoesNotBlameWindowsInfrastructure(string module)
    {
        var entry = Assert.IsType<BugCheckEntry>(BugCheckDatabase.Get(0xD1));

        var steps = BugCheckDatabase.BuildPrioritizedSolutions(entry, module);

        Assert.DoesNotContain(steps, step => step.Contains("转储直接指向", StringComparison.Ordinal));
    }

    [Fact]
    public void Guidance_DoesNotContainObsoleteOrUnsafeFirstLineAdvice()
    {
        var allText = string.Join('\n', BugCheckDatabase.All.SelectMany(entry => entry.Solutions));

        Assert.DoesNotContain("使用 WinDbg 或本工具", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("按 F8", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("调节 TDR 参数", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("禁用第三方杀毒软件测试", allText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StorageEntries_WarnToBackUpBeforeRepairWrites()
    {
        var storageEntries = BugCheckDatabase.All.Where(entry =>
            entry.Category.Contains("存储", StringComparison.Ordinal) ||
            entry.Category.Contains("磁盘", StringComparison.Ordinal));

        Assert.NotEmpty(storageEntries);
        Assert.All(storageEntries, entry =>
            Assert.Contains(entry.SafetyNotes, note => note.Contains("备份", StringComparison.Ordinal)));
    }
}
