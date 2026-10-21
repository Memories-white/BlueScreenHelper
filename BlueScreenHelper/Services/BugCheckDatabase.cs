using BlueScreenHelper.Models;

namespace BlueScreenHelper.Services;

public static class BugCheckDatabase
{
    private static readonly Dictionary<uint, BugCheckEntry> _entries = BuildEntries();

    public static BugCheckEntry? Get(uint code)
    {
        return _entries.TryGetValue(code, out var entry) ? entry : null;
    }

    public static IReadOnlyCollection<BugCheckEntry> All => _entries.Values;

    /// <summary>
    /// Builds an evidence-first action plan. A third-party module named by the
    /// dump is handled before the generic knowledge-base steps, while Windows
    /// kernel modules are deliberately not presented as the root cause.
    /// </summary>
    public static IReadOnlyList<string> BuildPrioritizedSolutions(
        BugCheckEntry entry,
        string? faultingModule)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var steps = new List<string>();
        if (IsActionableThirdPartyModule(faultingModule))
        {
            steps.Add($"转储直接指向 {faultingModule}：先从设备厂商或整机厂商官网下载匹配当前 Windows 版本的驱动；若问题始于一次更新，优先回滚该驱动。每次只更改一项并保留原安装包。");
        }

        steps.AddRange(entry.Solutions);
        return steps
            .Select(step => step.Trim())
            .Where(step => step.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> BuildGeneralSolutions(string? faultingModule = null)
    {
        var steps = new List<string>();
        if (IsActionableThirdPartyModule(faultingModule))
        {
            steps.Add($"转储指向 {faultingModule}：从设备或整机厂商官网更新该驱动；若蓝屏始于驱动更新，先回滚并复测。");
        }

        steps.AddRange(new[]
        {
            "记录停止代码、四个参数、发生时间和触发场景，并保留最新的 Minidump；不要只依据一次崩溃更换硬件。",
            "安装 Windows 更新后重启，再从整机或硬件厂商官网更新芯片组、存储、网络和显卡驱动；每次只更改一类驱动。",
            "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，成功后再运行 sfc /scannow。",
            "运行 Windows 内存诊断 (mdsched.exe)；若仍复现，再分条、分插槽测试内存。",
            "先运行 chkdsk /scan 做在线检查；只有发现文件系统错误且重要数据已备份时，才安排 chkdsk /f。"
        });
        return steps;
    }

    private static bool IsActionableThirdPartyModule(string? module)
    {
        if (string.IsNullOrWhiteSpace(module)) return false;

        var name = Path.GetFileName(module.Trim());
        string[] nonActionableModules =
        {
            "ntoskrnl.exe", "ntkrnlmp.exe", "hal.dll", "win32kfull.sys",
            "win32kbase.sys", "dxgkrnl.sys", "dxgmms2.sys", "watchdog.sys",
            "Wdf01000.sys", "fltmgr.sys", "ntfs.sys", "storport.sys",
            "stornvme.sys", "disk.sys", "classpnp.sys", "tcpip.sys", "ndis.sys",
            "acpi.sys", "pci.sys", "memory_corruption"
        };
        return !nonActionableModules.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static BugCheckEntry E(uint code, string name, string category, string desc,
        string[] causes, string[] solutions, string[]? drivers = null,
        string[]? verificationSteps = null, string[]? safetyNotes = null) => new()
        {
            Code = code,
            Name = name,
            Category = category,
            Description = desc,
            Causes = causes,
            Solutions = solutions,
            VerificationSteps = verificationSteps ?? DefaultVerificationSteps(category),
            SafetyNotes = safetyNotes ?? DefaultSafetyNotes(category),
            RelatedDrivers = drivers ?? Array.Empty<string>()
        };

    private static string[] DefaultVerificationSteps(string category)
    {
        if (category.Contains("存储", StringComparison.Ordinal) || category.Contains("磁盘", StringComparison.Ordinal))
            return new[] { "确认磁盘健康信息没有警告，chkdsk /scan 不再报告错误。", "完成两次冷启动，并在原读写场景下观察是否再次出现相同停止代码。" };
        if (category.Contains("内存", StringComparison.Ordinal) || category.Contains("硬件", StringComparison.Ordinal))
            return new[] { "完成 Windows 内存诊断；若有多条内存，分条、分插槽复测以确认故障是否随部件移动。", "恢复默认频率后在原场景运行至少一个完整工作周期，确认没有新的相同停止代码。" };
        if (category.Contains("显卡", StringComparison.Ordinal) || category.Contains("图形", StringComparison.Ordinal))
            return new[] { "在原游戏、视频或 GPU 负载场景复测，确认不再黑屏、驱动重置或蓝屏。", "检查可靠性监视器和系统日志，确认没有新增显示驱动超时记录。" };
        if (category.Contains("启动", StringComparison.Ordinal))
            return new[] { "连续完成两次冷启动和一次重启，确认系统盘始终可访问。", "确认 Windows 恢复环境仍可进入，并记录修复前后的固件启动与控制器设置。" };

        return new[] { "重现原触发场景并观察至少一个完整工作周期，确认停止代码未再次出现。", "若仍蓝屏，对比新旧转储中的停止代码和故障模块，确认线索是否发生变化。" };
    }

    private static string[] DefaultSafetyNotes(string category)
    {
        var notes = new List<string>
        {
            "一次只执行一项改动并记录时间，便于确认真正有效的修复。",
            "驱动和固件只从 Windows Update、整机厂商或硬件厂商官方渠道获取。"
        };
        if (category.Contains("存储", StringComparison.Ordinal) || category.Contains("磁盘", StringComparison.Ordinal))
            notes.Add("磁盘出现异响、掉盘或健康警告时先备份；不要在可疑故障盘上反复执行 /f、/r 或写入型压力测试。");
        if (category.Contains("驱动", StringComparison.Ordinal))
            notes.Add("Driver Verifier 会主动触发蓝屏，仅适合有恢复方案的测试环境；普通排查不要对全部驱动启用。");
        if (category.Contains("硬件", StringComparison.Ordinal) || category.Contains("CPU", StringComparison.Ordinal))
            notes.Add("压力测试期间监控温度并随时停止异常测试；刷写 BIOS 前保持稳定供电并核对准确机型。");
        return notes.ToArray();
    }

    private static Dictionary<uint, BugCheckEntry> BuildEntries()
    {
        var list = new List<BugCheckEntry>();

        list.Add(E(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL", "驱动程序", "驱动程序在过高的 IRQL 级别上访问了不可分页内存，通常是驱动程序缺陷或内存损坏导致。",
            new[] { "第三方驱动程序存在缺陷", "内存（RAM）物理损坏或接触不良", "驱动程序与系统组件发生内存地址冲突", "BIOS/固件设置不合理（如内存时序）" },
            new[] { "查看崩溃参数 4 和本工具识别的故障模块，优先更新或回滚对应的第三方驱动", "运行 Windows 内存诊断 (mdsched.exe)，若报错则分条、分插槽复测", "若无法定位模块，按发生时间核对最近新增的驱动、设备或系统更新", "仅在厂商说明与当前机型匹配时更新 BIOS", "取消 CPU、GPU 和内存超频，恢复固件默认值后复测" }));

        list.Add(E(0x0000001A, "MEMORY_MANAGEMENT", "内存/系统", "内存管理器检测到严重错误，通常表示内存损坏、驱动错误或文件系统问题。",
            new[] { "物理内存（RAM）故障", "驱动程序对内存的错误操作", "磁盘文件系统错误", "系统文件损坏" },
            new[] { "运行 mdsched.exe；若报错，分条、分插槽复测内存", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，成功后运行 sfc /scannow", "先运行 chkdsk /scan；确认有文件系统错误并备份后再安排 chkdsk /f", "根据转储模块和发生时间定向更新或回滚驱动，不要一次性更新全部驱动", "恢复内存默认频率和时序；故障随某条内存移动时再更换该内存" }));

        list.Add(E(0x0000001E, "KMODE_EXCEPTION_NOT_HANDLED", "驱动程序", "内核模式程序产生了处理器无法处理的异常，通常是无效指令或内存访问错误。",
            new[] { "驱动程序缺陷或版本不兼容", "系统服务或内核组件损坏", "磁盘错误导致代码页损坏", "硬件冲突" },
            new[] { "分析转储中的异常地址和故障模块，更新或回滚对应第三方驱动", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow", "先运行 chkdsk /scan；仅在发现错误且备份后安排 chkdsk /f", "若问题紧随软件或驱动安装出现，进入安全模式卸载该项后复测" }));

        list.Add(E(0x00000024, "NTFS_FILE_SYSTEM", "存储/磁盘", "NTFS 文件系统驱动遇到错误，多与磁盘损坏、磁盘驱动或控制器问题有关。",
            new[] { "硬盘或 SSD 出现坏道/物理损坏", "磁盘控制器或存储驱动缺陷", "断电导致文件系统元数据损坏", "第三方案件与 NTFS 驱动冲突" },
            new[] { "立即备份重要数据，再运行 chkdsk /scan 做在线检查", "读取磁盘厂商诊断工具或 SMART 健康信息；出现警告、掉盘或异响时停止写入型测试", "从整机或主板厂商官网更新芯片组、存储控制器驱动和 SSD 固件", "仅在确认文件系统错误且已备份后安排 chkdsk /f；/r 会长时间读取全盘，不应在疑似故障盘上反复运行", "健康信息异常或错误持续增长时更换磁盘并恢复备份" }));

        list.Add(E(0x0000003B, "SYSTEM_SERVICE_EXCEPTION", "驱动程序/系统", "系统服务在执行过程中产生异常。在 Win10/11 上常见于驱动缺陷、内存故障或系统更新问题。",
            new[] { "显卡/声卡/网卡等驱动缺陷", "系统更新不完整或损坏", "内存故障", "系统文件损坏" },
            new[] { "分析故障模块，更新对应驱动", "运行 sfc /scannow 和 DISM /Online /Cleanup-Image /RestoreHealth", "卸载最近安装的系统更新", "运行内存诊断" }));

        list.Add(E(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA", "驱动程序/内存", "系统引用了不可分页区域的无效内存地址。常见于驱动缺陷或内存故障。",
            new[] { "驱动程序使用无效内存指针", "物理内存故障", "磁盘/页面文件问题", "杀毒软件或安全软件冲突" },
            new[] { "检查故障模块，更新或回滚对应驱动；若涉及第三方安全软件，优先使用厂商卸载工具完整移除后复测", "运行 Windows 内存诊断 (mdsched.exe)，报错时分条复测", "确认页面文件为系统管理且系统盘有足够可用空间", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow" }));

        list.Add(E(0x0000007B, "INACCESSIBLE_BOOT_DEVICE", "存储/启动", "Windows 在启动过程中无法访问系统分区，通常与磁盘、控制器或启动配置有关。",
            new[] { "硬盘/SSD 连接松动或损坏", "磁盘控制器驱动问题", "MBR/BOOTMGR 损坏", "BIOS 中启动模式（AHCI/IDE）设置改变" },
            new[] { "从 Windows 恢复环境进入“疑难解答 → 高级选项 → 启动修复”", "若问题始于存储驱动或系统更新，在恢复环境中先卸载对应更新或进入安全模式回滚驱动", "对照故障前记录检查 UEFI/BIOS 的存储控制器模式；不要盲目切换 AHCI、RAID 或 VMD，否则可能继续无法启动", "台式机断电后检查系统盘数据线、电源线和插槽；笔记本按厂商诊断流程检查 SSD", "先备份重要数据并检查磁盘健康；仅在启动配置确有损坏时再使用 bootrec /scanos 与 bootrec /rebuildbcd" }));

        list.Add(E(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "驱动程序/系统", "系统线程产生未处理异常，硬件不兼容或驱动缺陷是常见诱因。",
            new[] { "驱动程序缺陷（尤其显卡驱动）", "硬件不兼容或超频", "内存故障", "系统文件损坏" },
            new[] { "更新或回滚显卡驱动", "恢复 CPU/内存默认频率", "运行内存诊断", "进入安全模式排查第三方驱动" }));

        list.Add(E(0x0000007F, "UNEXPECTED_KERNEL_MODE_TRAP", "硬件/CPU", "内核产生意外陷阱，通常与硬件故障、CPU 超频或散热问题有关。",
            new[] { "CPU 故障或超频不稳定", "散热不良导致过热", "内存故障", "主板供电问题" },
            new[] { "检查散热器、风扇和温度，断电后清理灰尘", "恢复 CPU 和内存默认频率、电压及时序", "先运行内存诊断；需要压力测试时全程监控温度并在异常时立即停止", "仅从主板或整机厂商获取匹配准确机型的 BIOS，并在稳定供电下更新" }));

        list.Add(E(0x0000008E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", "驱动程序", "内核模式程序产生异常而系统无法处理，多为驱动或内存问题。",
            new[] { "驱动程序缺陷", "硬件内存故障", "软件与系统不兼容" },
            new[] { "分析故障模块并更新驱动", "运行内存诊断", "卸载最近安装的软件" }));

        list.Add(E(0x0000009C, "MACHINE_CHECK_EXCEPTION", "硬件", "CPU 检测到硬件错误并报告，属于机器检查异常（MCE），几乎总是硬件问题。",
            new[] { "CPU/内存超频过度", "内存或 CPU 硬件故障", "电源供电不足", "主板故障或 BIOS 缺陷" },
            new[] { "恢复所有默认频率与电压", "检查电源功率与稳定性", "更新 BIOS", "运行内存和 CPU 稳定性测试", "若持续出现，送修或更换硬件" }));

        list.Add(E(0x0000009F, "DRIVER_POWER_STATE_FAILURE", "电源/驱动", "驱动在处理电源状态转换（睡眠/唤醒/关机）时出错，常见于笔记本的电源管理问题。",
            new[] { "驱动程序未正确处理电源事件", "PCIe/USB 设备电源管理驱动缺陷", "固件与系统电源策略不兼容" },
            new[] { "更新主板/BIOS、显卡、网卡驱动", "使用电源选项诊断，临时禁用选择性挂起", "检查最近是否有驱动更新引发", "在设备管理器中关闭问题设备的电源管理节能选项" }));

        list.Add(E(0x000000C2, "BAD_POOL_CALLER", "驱动程序", "线程对内存池执行了非法操作，通常是驱动在错误的池类型上调用 API。",
            new[] { "驱动程序池操作错误", "驱动与系统版本不兼容", "内存损坏" },
            new[] { "识别故障驱动并从厂商官网更新；若问题始于更新则回滚或卸载", "安装 Windows 累积更新后复测", "运行 Windows 内存诊断排除随机内存损坏", "只有在普通排查无法定位且已准备安全模式/恢复环境时，才对少量可疑的非 Microsoft 驱动使用 Driver Verifier；完成后运行 verifier /reset 关闭" }));

        list.Add(E(0x000000C4, "DRIVER_VERIFIER_DETECTED_VIOLATION", "驱动程序", "驱动验证器检测到驱动程序违规操作。若自行开启过验证器，说明目标驱动确有问题；否则多为第三方驱动缺陷。",
            new[] { "被验证的驱动程序存在缺陷", "驱动使用了错误的内存分配方式" },
            new[] { "查看转储或验证器报告的违规驱动，从设备/整机厂商官网更新或回滚该驱动", "使用 verifier /querysettings 保存当前验证设置，完成取证后运行 verifier /reset 并重启关闭验证", "若系统循环蓝屏，从 Windows 恢复环境进入安全模式后执行 verifier /reset", "不要对全部驱动长期启用 Driver Verifier；它会增加系统开销并主动触发蓝屏" }));

        list.Add(E(0x000000C5, "DRIVER_CORRUPTED_EXPOOL", "驱动程序", "驱动程序损坏了系统池内存，使其指向错误位置。",
            new[] { "驱动程序内存操作越界", "驱动被恶意软件篡改", "内存故障" },
            new[] { "更新或卸载可疑驱动", "运行内存诊断", "使用杀毒软件全盘扫描", "运行 sfc /scannow" }));

        list.Add(E(0x000000D1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "驱动程序", "驱动程序在错误的 IRQL 级别访问内存，是网络/存储/显卡驱动缺陷的常见表现。",
            new[] { "网卡/显卡/存储等驱动缺陷", "驱动 DMA 操作错误", "内存损坏" },
            new[] { "根据参数 4 定位故障驱动并更新", "尝试卸载最近更新的驱动", "运行内存诊断", "更新系统补丁" }));

        list.Add(E(0x000000D5, "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "驱动程序", "驱动访问了已释放的内存（悬空指针），通常是驱动 bug 或内存损坏。",
            new[] { "驱动悬空指针", "内存故障", "驱动版本与系统不兼容" },
            new[] { "更新对应驱动", "运行内存诊断", "回滚最近安装的驱动" }));

        list.Add(E(0x000000D8, "DRIVER_USED_EXCESSIVE_POWER", "驱动程序", "驱动在 DPC 中运行时间过长，多与有缺陷的驱动或固件有关。",
            new[] { "驱动 DPC 执行时间过长", "固件缺陷" },
            new[] { "更新主板/显卡等驱动", "更新 BIOS/固件" }));

        list.Add(E(0x000000EA, "THREAD_STUCK_IN_DEVICE_DRIVER", "显卡驱动", "显卡驱动导致系统线程卡死（显示驱动 hung），与 GPU 驱动或硬件有关。",
            new[] { "显卡驱动缺陷或崩溃", "GPU 过热或硬件故障", "显卡超频", "多显示器/混合显卡切换问题" },
            new[] { "先使用显卡厂商安装程序执行清洁安装；仍失败时创建还原点，再在安全模式谨慎使用 DDU 后安装整机/显卡厂商稳定版驱动", "检查 GPU 温度、风扇、供电线和散热", "恢复 GPU 与显存默认频率", "从整机/主板厂商官网更新芯片组与适用的 BIOS", "笔记本应优先安装整机厂商提供的核显、独显及混合显卡驱动组合" }));

        list.Add(E(0x000000EF, "CRITICAL_PROCESS_DIED", "系统/软件", "关键系统进程意外终止，如 csrss、wininit（Win10/11）或 smss 崩溃。",
            new[] { "系统文件损坏", "软件冲突导致关键进程被杀", "存储故障导致系统文件读取失败", "恶意软件" },
            new[] { "运行 sfc /scannow 与 DISM 修复", "检查磁盘健康状态", "检查近期安装的软件并卸载", "全盘查杀病毒", "系统还原至正常时间点" }));

        list.Add(E(0x000000F4, "CRITICAL_OBJECT_TERMINATION", "系统/存储", "系统关键对象（进程/线程）异常终止，常与存储设备或系统服务故障有关。",
            new[] { "系统盘故障或坏道", "驱动/服务崩溃导致关键进程终止", "内存故障", "注册表损坏" },
            new[] { "立即备份重要数据并检查硬盘/SSD 健康状态；台式机断电后检查 SATA 与电源线", "先运行 chkdsk /scan，确认文件系统错误且已备份后再安排 chkdsk /f", "结合转储与事件日志确认被意外终止的是存储、系统还是其他关键进程", "从整机/主板厂商官网更新存储、芯片组驱动和适用的 SSD 固件" }));

        list.Add(E(0x000000F7, "DRIVER_OVERRAN_STACK_BUFFER", "驱动程序", "驱动发生栈缓冲区溢出，可能是驱动缺陷或有意的缓冲区溢出攻击。",
            new[] { "驱动程序栈溢出缺陷", "恶意软件利用驱动漏洞" },
            new[] { "根据转储模块和发生时间更新或回滚可疑驱动", "使用 Windows 安全中心执行完整扫描或 Microsoft Defender 离线扫描", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow", "仅在有恢复方案时对少量可疑第三方驱动使用 Driver Verifier，完成后执行 verifier /reset" }));

        list.Add(E(0x00000101, "CLOCK_WATCHDOG_TIMEOUT", "CPU/硬件", "处理器在时钟间隔内未响应中断。多由超频、电源管理或 CPU 故障引起。",
            new[] { "CPU 超频/降压不稳定", "电源管理（C 状态）问题", "CPU 硬件故障", "主板供电问题" },
            new[] { "恢复 CPU、内存默认频率与电压，先加载固件默认值复测", "检查 CPU 温度、散热器安装和主板供电；笔记本运行厂商硬件诊断", "仅从主板/整机厂商获取匹配机型的 BIOS 与芯片组驱动", "检查电源额定功率、供电线和稳定性", "需要压力测试时从短时测试开始并全程监控温度，出现异常立即停止" }));

        list.Add(E(0x00000109, "CRITICAL_STRUCTURE_CORRUPTION", "系统/硬件", "内核检测到关键结构损坏，通常被视为内核完整性受影响，与硬件或驱动有关。",
            new[] { "内存故障", "驱动程序破坏内核结构", "超频不稳定", "固件（BIOS/UEFI）缺陷" },
            new[] { "运行内存诊断", "更新或移除可疑驱动", "恢复默认频率", "更新 BIOS", "考虑重装系统排除软件层面因素" }));

        list.Add(E(0x0000010E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "显卡", "视频内存管理器内部错误，几乎总是显卡驱动或显卡硬件问题。",
            new[] { "显卡驱动缺陷", "显存故障", "GPU 过热或超频" },
            new[] { "先用显卡厂商安装程序执行清洁安装；仍失败时创建还原点，再在安全模式使用 DDU 后安装稳定版驱动", "检查 GPU 温度、风扇和供电", "恢复 GPU 与显存默认频率", "可用时测试核显或另一张显卡，确认故障是否随硬件移动" }));

        list.Add(E(0x00000113, "VIDEO_DXGKRNL_FATAL_ERROR", "显卡", "DirectX 图形内核驱动 (dxgkrnl) 检测到致命错误。",
            new[] { "显卡驱动缺陷", "GPU 硬件故障", "超频导致不稳定" },
            new[] { "更新/回滚显卡驱动", "降低 GPU 频率测试", "检查电源与 GPU 供电接口" }));

        list.Add(E(0x00000116, "VIDEO_TDR_FAILURE", "显卡", "显示驱动响应超时（TDR），显卡驱动在限制时间内未响应操作系统。这是最常见的显卡相关蓝屏之一。",
            new[] { "显卡驱动崩溃或死锁", "GPU 过热/供电不足", "显卡硬件故障或超频", "驱动程序与游戏/应用不兼容" },
            new[] { "先用显卡厂商安装程序执行清洁安装；仍失败时创建还原点，再在安全模式使用 DDU 后安装稳定版驱动", "检查 GPU 温度、散热、供电和显卡插槽", "恢复 GPU 与显存默认频率并关闭叠加层/注入类工具后复测", "从整机或主板厂商官网更新芯片组与适用的 BIOS", "笔记本应安装整机厂商验证过的核显、独显和混合显卡驱动组合", "不要把提高 TdrDelay 或关闭 TDR 当作修复；这会掩盖驱动或硬件故障并延长卡死时间" }));

        list.Add(E(0x00000124, "WHEA_UNCORRECTABLE_ERROR", "硬件", "Windows 硬件错误架构 (WHEA) 报告了无法纠正的硬件错误。强烈指向硬件问题（CPU、内存、主板、电源）。",
            new[] { "CPU/内存超频过度", "内存（RAM）硬件故障", "CPU 或主板故障", "电源供电不稳定", "散热不良" },
            new[] { "恢复所有默认频率与电压（重点排查内存 XMP/EXPO）", "逐个测试内存条，优先单条启动", "检查散热与供电", "更新 BIOS", "检查事件日志中 WHEA-Logger 事件（17/18/19）获取更多信息", "必要时送修或更换硬件" }));

        list.Add(E(0x00000133, "DPC_WATCHDOG_VIOLATION", "存储/驱动", "DPC 看门狗超时，通常与存储驱动、SSD 或卷管理相关，在 NVMe 系统上较常见。",
            new[] { "存储驱动或 NVMe 固件问题", "SSD 硬件故障", "RAID/卷驱动冲突", "电源管理导致设备挂起" },
            new[] { "更新存储驱动与 SSD 固件", "检查磁盘健康状态", "更新 BIOS 中 NVMe/SATA 相关设置", "更新芯片组驱动", "若使用 RAID，更新 RAID 驱动" }));

        list.Add(E(0x00000135, "REGISTRY_FILTER_DRIVER_EXCEPTION", "系统/驱动", "注册表筛选器驱动发生异常，通常是安全软件（杀毒/加密）或设备过滤驱动导致。",
            new[] { "杀毒/安全软件与系统不兼容", "注册表过滤驱动缺陷", "系统文件损坏" },
            new[] { "卸载或更新第三方杀毒软件", "运行 sfc /scannow", "进入安全模式排查" }));

        list.Add(E(0x00000139, "KERNEL_SECURITY_CHECK_FAILURE", "驱动/内存", "内核安全运行时检查失败，表示内存被意外修改，常与驱动或硬件有关。",
            new[] { "内存损坏（驱动越界写）", "物理内存故障", "存储驱动问题", "恶意软件" },
            new[] { "更新或移除可疑驱动", "运行内存诊断", "运行 sfc /scannow", "全盘杀毒" }));

        list.Add(E(0x0000013A, "KERNEL_MODE_HEAP_CORRUPTION", "驱动程序", "内核模式堆被破坏，通常是驱动内存操作错误。",
            new[] { "驱动堆溢出/悬空指针", "内存硬件故障" },
            new[] { "根据转储模块更新或回滚可疑驱动", "运行 Windows 内存诊断，报错时分条复测", "普通排查无结果且已准备恢复环境时，才对少量可疑第三方驱动使用 Driver Verifier；完成后执行 verifier /reset" }));

        list.Add(E(0x00000142, "VIDEO_DEVICE_TDR_FAILURE", "显卡", "视频设备 TDR 故障，常见于显卡硬件或驱动。",
            new[] { "显卡驱动崩溃", "GPU 硬件故障", "显卡超频/过热" },
            new[] { "先执行显卡驱动清洁安装；仍失败时创建还原点，再在安全模式谨慎使用 DDU", "检查 GPU 温度、散热和供电", "恢复 GPU 与显存默认频率" }));

        list.Add(E(0x00000154, "UNEXPECTED_STORE_EXCEPTION", "存储/SSD", "存储组件（Store）产生意外异常，通常与存储设备、存储驱动或 RAID 配置有关。",
            new[] { "SSD/存储设备故障", "存储驱动缺陷", "固件或磁盘加密组件问题", "内存故障" },
            new[] { "先备份重要数据，再检查 SSD 健康状态和厂商固件更新", "从整机/主板厂商官网更新存储与芯片组驱动", "运行 Windows 内存诊断排除缓存数据损坏", "先运行 chkdsk /scan；确认文件系统错误并备份后再安排 chkdsk /f" }));

        list.Add(E(0x00000158, "ECC_HARDWARE_CORRUPTION", "硬件/ECC", "支持 ECC 的内存报告了硬件级数据损坏。",
            new[] { "ECC 内存硬件故障", "内存接触不良" },
            new[] { "检查内存条与插槽", "更新 BIOS", "更换内存" }));

        list.Add(E(0x0000021A, "WIN32K_CRITICAL_FAILURE", "系统/图形", "Win32k 子系统发生致命错误，可能与图形驱动或其他内核组件冲突有关。",
            new[] { "系统文件损坏", "视频驱动问题", "第三方软件注入 win32k" },
            new[] { "运行 sfc /scannow 与 DISM", "更新显卡驱动", "进入安全模式排查第三方软件" }));

        list.Add(E(0x0000021B, "EXFAT_FILE_SYSTEM", "存储", "exFAT 文件系统驱动遇到错误，多与外接存储设备或存储驱动有关。",
            new[] { "U 盘/移动硬盘损坏（exFAT 格式）", "存储控制器驱动问题", "异常断电导致卷损坏" },
            new[] { "先备份该卷数据并运行 chkdsk /scan；确认错误后再安排 chkdsk /f", "检查外接存储设备健康、数据线、供电和接口", "从整机或控制器厂商官网更新存储驱动" }));

        list.Add(E(0x00000076, "PROCESS_HAS_LOCKED_PAGES", "驱动程序", "驱动在处理 I/O 后未正确解锁内存页。",
            new[] { "驱动未释放锁定的内存页", "驱动缺陷" },
            new[] { "更新对应驱动", "检查转储中的故障模块" }));

        list.Add(E(0x00000077, "KERNEL_STACK_INPAGE_ERROR", "存储/页面文件", "系统无法从页面文件或磁盘读取内核栈数据，多为磁盘故障。",
            new[] { "硬盘/SSD 坏道或故障", "页面文件所在磁盘出现问题", "磁盘控制器驱动错误" },
            new[] { "立即备份重要数据并检查磁盘健康信息", "先运行 chkdsk /scan；仅在磁盘健康正常、已备份且确认文件系统错误时安排 chkdsk /f", "若出现健康警告、掉盘或异响，停止 /r 等长时间全盘读取并更换磁盘", "从整机/主板厂商官网更新磁盘控制器和芯片组驱动" }));

        list.Add(E(0x000000B4, "VIDEO_DRIVER_INIT_FAILURE", "显卡驱动", "显卡驱动初始化失败，常因驱动损坏或显卡硬件问题。",
            new[] { "显卡驱动损坏或不兼容", "显卡硬件故障", "注册表驱动信息损坏" },
            new[] { "进入安全模式回滚显卡驱动；仍失败时创建还原点，再谨慎使用 DDU 后安装厂商稳定版驱动", "断电后检查显卡供电线与插槽，并检查 GPU 温度", "仅从主板/整机厂商获取匹配准确机型的 BIOS" }));

        list.Add(E(0x000000BE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", "驱动程序", "驱动尝试写入只读内存。",
            new[] { "驱动内存属性错误", "驱动缺陷" },
            new[] { "更新/卸载故障驱动", "运行内存诊断" }));

        list.Add(E(0x000000DE, "POOL_CORRUPTION_IN_FILE_AREA", "驱动程序", "文件系统区域中的内存池被破坏。",
            new[] { "驱动内存越界", "磁盘错误", "内存故障" },
            new[] { "根据转储模块更新或回滚故障驱动", "先运行 chkdsk /scan；确认错误且备份后再安排 chkdsk /f", "运行 Windows 内存诊断，报错时分条复测" }));

        list.Add(E(0x00000080, "NMI_HARDWARE_FAILURE", "硬件", "系统收到不可屏蔽中断（NMI），表示硬件电路故障。",
            new[] { "内存/主板硬件故障", "电源故障", "固件问题" },
            new[] { "检查内存与插槽", "更新 BIOS", "检查电源", "送修硬件" }));

        list.Add(E(0x000000E3, "RESOURCE_NOT_OWNED", "驱动程序", "驱动试图释放不拥有的资源。",
            new[] { "驱动资源管理错误", "驱动缺陷" },
            new[] { "更新或回滚相关驱动", "普通排查无法定位且已准备恢复环境时，才对少量可疑第三方驱动启用 Driver Verifier；完成后执行 verifier /reset" }));

        list.Add(E(0x000000F1, "SCSI_VERIFIER_DETECTED_VIOLATION", "驱动/存储", "SCSI 驱动验证器检测到违规，与存储类驱动的内存操作有关。",
            new[] { "存储驱动缺陷", "磁盘控制器驱动问题" },
            new[] { "更新存储/芯片组驱动", "检查磁盘健康" }));

        list.Add(E(0x0000010F, "RESOURCE_MANAGER_EXCEPTION_NOT_HANDLED", "系统", "资源管理器异常未处理，多与内核资源问题相关。",
            new[] { "资源许可/句柄管理错误", "驱动问题" },
            new[] { "更新系统与驱动", "运行 sfc /scannow" }));

        list.Add(E(0x00000119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "显卡", "视频调度器内部错误，与 GPU 驱动或硬件有关。",
            new[] { "显卡驱动缺陷", "GPU 硬件故障或显存问题", "超频" },
            new[] { "先执行显卡驱动清洁安装；仍失败时创建还原点，再在安全模式谨慎使用 DDU", "恢复 GPU 与显存默认频率", "检查 GPU 温度、风扇和供电" }));

        list.Add(E(0x0000014F, "SHADOW_STACK_VIOLATION", "系统/CPU", "硬件强制栈保护（CET Shadow Stack）被违反，可能表示驱动/软件篡改返回地址。",
            new[] { "不兼容的驱动或软件", "内存损坏", "恶意软件" },
            new[] { "更新所有驱动与软件", "运行 sfc /scannow", "全盘杀毒" }));

        list.Add(E(0x00000018, "REFERENCE_BY_POINTER", "驱动程序", "对象引用计数不一致，通常表示内核驱动重复释放对象或未正确维护引用。",
            new[] { "第三方驱动错误管理内核对象", "过滤驱动或安全软件冲突", "内存损坏导致对象状态异常" },
            new[] { "根据转储中的故障模块更新或回滚对应第三方驱动", "若问题紧随安全、备份、虚拟化或设备管理软件安装出现，完整卸载该软件后复测", "运行 Windows 内存诊断排除随机内存损坏", "普通排查无结果且已准备恢复环境时，才对少量可疑驱动使用 Driver Verifier" }));

        list.Add(E(0x00000019, "BAD_POOL_HEADER", "驱动程序/内存", "内核内存池头部损坏，常见于驱动越界写入、重复释放或物理内存错误。",
            new[] { "第三方驱动破坏内存池", "RAM 不稳定或损坏", "超频或内存时序不稳定", "安全软件/文件系统过滤驱动冲突" },
            new[] { "按转储模块与安装时间定位并回滚可疑驱动", "恢复 CPU 与内存默认频率及时序", "运行 Windows 内存诊断，报错时分条、分插槽复测", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow" }));

        list.Add(E(0x00000034, "CACHE_MANAGER", "存储/内存", "文件系统缓存管理器遇到无法恢复的错误，可能来自存储 I/O、文件系统过滤驱动或内存损坏。",
            new[] { "磁盘或存储控制器 I/O 错误", "杀毒、备份或加密过滤驱动缺陷", "RAM 损坏导致缓存数据异常", "系统文件或文件系统损坏" },
            new[] { "先备份重要数据并检查磁盘厂商健康信息", "运行 chkdsk /scan；确认文件系统错误且已备份后再安排 chkdsk /f", "更新或回滚存储、备份、加密及安全软件的过滤驱动", "运行 Windows 内存诊断", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow" }));

        list.Add(E(0x0000004E, "PFN_LIST_CORRUPT", "内存/驱动", "页帧号列表被破坏，通常由 RAM 错误或驱动错误操作物理页导致。",
            new[] { "物理内存损坏或超频不稳定", "驱动重复释放或破坏内存页", "DMA 设备或驱动写入错误地址" },
            new[] { "恢复内存默认频率及时序并运行 Windows 内存诊断", "多条内存时分条、分插槽复测", "根据转储模块更新或回滚显卡、网卡、存储等 DMA 驱动", "若错误稳定跟随某条内存移动，再更换该内存" }));

        list.Add(E(0x00000051, "REGISTRY_ERROR", "系统/存储", "注册表组件遇到严重 I/O 或一致性错误，可能与磁盘、内存或注册表配置单元损坏有关。",
            new[] { "系统盘 I/O 错误或空间不足", "注册表配置单元损坏", "内存损坏", "备份、安全或磁盘过滤驱动冲突" },
            new[] { "立即备份重要数据并确认系统盘有足够可用空间", "检查磁盘厂商健康信息并运行 chkdsk /scan", "从 Windows 恢复环境尝试启动修复或系统还原，不要从其他计算机直接复制注册表文件", "运行 Windows 内存诊断", "进入系统后运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow" }));

        list.Add(E(0x0000006B, "PROCESS1_INITIALIZATION_FAILED", "系统/启动", "Windows 启动关键进程初始化失败，常见于系统文件、启动更新或磁盘数据损坏。",
            new[] { "系统更新中断或回滚失败", "关键系统文件损坏", "系统盘文件系统/I/O 错误", "启动阶段第三方过滤驱动冲突" },
            new[] { "进入 Windows 恢复环境运行启动修复", "若问题紧随系统更新出现，在高级选项中卸载最近的质量更新", "先备份数据并检查系统盘健康，再运行离线磁盘检查", "可进入安全模式时卸载最近新增的安全、存储或过滤驱动", "保留恢复密钥后再进行涉及 BitLocker 卷的离线修复" }));

        list.Add(E(0x00000074, "BAD_SYSTEM_CONFIG_INFO", "系统/启动", "启动所需的系统配置、注册表或引导数据不一致，导致 Windows 无法继续运行。",
            new[] { "注册表配置单元损坏", "引导配置数据错误", "系统更新或驱动安装中断", "内存或系统盘故障" },
            new[] { "进入 Windows 恢复环境运行启动修复", "若问题始于更新或驱动安装，使用高级选项卸载更新或进入安全模式回滚驱动", "使用系统还原恢复到已知正常的还原点", "先备份数据并检查系统盘健康与内存；不要直接套用来源不明的注册表修复脚本", "仅在确认引导配置损坏时使用 bootrec /scanos 与 bootrec /rebuildbcd" }));

        list.Add(E(0x000000A0, "INTERNAL_POWER_ERROR", "电源/驱动", "电源策略管理器遇到致命错误，常见于休眠文件、设备电源驱动或固件不兼容。",
            new[] { "设备驱动无法完成电源状态转换", "休眠文件损坏或空间不足", "BIOS/UEFI 与 ACPI 固件问题", "系统盘 I/O 错误" },
            new[] { "记录蓝屏发生在睡眠、唤醒、休眠还是关机阶段，并更新对应设备驱动", "以管理员身份运行 powercfg /h off，重启后再运行 powercfg /h on 重建休眠文件", "确认系统盘空间充足并运行 chkdsk /scan", "从整机厂商官网更新芯片组、电源管理驱动和适用的 BIOS", "若问题始于某次驱动更新，回滚该驱动后复测" }));

        list.Add(E(0x000000A5, "ACPI_BIOS_ERROR", "固件/启动", "固件提供的 ACPI 表或方法不符合 Windows 要求，通常是 BIOS/UEFI 缺陷或配置问题。",
            new[] { "BIOS/UEFI ACPI 实现缺陷", "固件设置或升级状态异常", "主板芯片组与当前 Windows 版本兼容性问题" },
            new[] { "加载固件默认设置并记录原设置后复测", "核对准确机型和硬件版本，仅从整机/主板厂商获取适用的 BIOS", "更新整机厂商提供的芯片组和电源管理驱动", "若故障始于 BIOS 更新，按厂商支持流程确认能否安全回退", "持续出现时联系整机或主板厂商并提供停止代码与固件版本" }));

        list.Add(E(0x000000CE, "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", "驱动程序", "驱动卸载时仍有未取消的计时器、DPC 或工作项，随后执行了已卸载代码。",
            new[] { "第三方驱动卸载流程缺陷", "设备热插拔或休眠恢复触发竞态", "旧版过滤驱动与当前系统不兼容" },
            new[] { "依据转储中已卸载的模块名更新或回滚对应驱动", "若与热插拔、睡眠或关机相关，更新设备固件和芯片组驱动后复测", "卸载最近新增的设备管理、虚拟化或过滤软件", "不要手动删除 .sys 文件；使用设备或软件的正式卸载程序" }));

        list.Add(E(0x000000E6, "DRIVER_VERIFIER_DMA_VIOLATION", "驱动/DMA", "Driver Verifier 或 IOMMU 检测到设备驱动执行了非法 DMA 操作。",
            new[] { "显卡、网卡、存储或扩展卡驱动 DMA 缺陷", "设备固件或 BIOS/IOMMU 兼容性问题", "正在运行 Driver Verifier" },
            new[] { "根据转储中的违规驱动，从设备或整机厂商官网更新或回滚该驱动", "完成取证后运行 verifier /reset 并重启，避免验证器持续触发蓝屏", "更新芯片组驱动、设备固件和适用的 BIOS", "若问题紧随新硬件出现，断电后重新安装该设备或暂时移除复测" }));

        list.Add(E(0x000000ED, "UNMOUNTABLE_BOOT_VOLUME", "存储/启动", "Windows 无法装载系统卷，可能是文件系统损坏、存储设备故障或控制器配置改变。",
            new[] { "系统盘文件系统严重损坏", "SSD/硬盘或控制器故障", "存储控制器模式或驱动改变", "异常断电或连接松动" },
            new[] { "进入 Windows 恢复环境并优先备份重要数据", "检查磁盘厂商健康信息；出现健康警告、掉盘或异响时停止修复写入并更换磁盘", "对照故障前记录检查 UEFI/BIOS 存储模式，不要盲目切换 AHCI、RAID 或 VMD", "磁盘健康正常且数据已备份时，再从恢复环境运行 chkdsk /f", "台式机断电后检查数据线、电源线和接口" }));

        list.Add(E(0x000000FC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "驱动程序/安全", "内核代码尝试从不可执行内存区域运行指令，常见于驱动损坏、越界写入或恶意代码。",
            new[] { "第三方驱动跳转到无效内存", "内存损坏", "系统或驱动文件被篡改", "不兼容的底层安全/虚拟化软件" },
            new[] { "根据转储模块更新或回滚对应第三方驱动", "运行 Windows 内存诊断并恢复默认内存频率", "使用 Windows 安全中心执行完整扫描或 Microsoft Defender 离线扫描", "以管理员身份先运行 DISM /Online /Cleanup-Image /RestoreHealth，再运行 sfc /scannow", "卸载最近新增的底层安全、反作弊或虚拟化软件后复测" }));

        list.Add(E(0x0000012B, "FAULTY_HARDWARE_CORRUPTED_PAGE", "内存/硬件", "Windows 检测到单比特内存错误或硬件损坏的内存页，优先怀疑 RAM、内存控制器或超频不稳定。",
            new[] { "RAM 物理故障", "内存超频/XMP/EXPO 不稳定", "CPU 内存控制器或主板插槽问题", "少数情况下为 DMA 驱动写坏内存" },
            new[] { "关闭 XMP/EXPO 和所有超频，加载固件默认值后复测", "运行 Windows 内存诊断；多条内存时分条、分插槽测试", "重新安装内存并检查插槽；故障随某条内存移动时更换该内存", "更新匹配准确机型的 BIOS 与芯片组驱动", "若各内存条单独测试仍在同一插槽报错，检查主板、CPU 插座和内存控制器" }));

        var dict = new Dictionary<uint, BugCheckEntry>();
        foreach (var e in list) dict[e.Code] = e;
        return dict;
    }
}
