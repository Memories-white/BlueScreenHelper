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

    private static BugCheckEntry E(uint code, string name, string category, string desc,
        string[] causes, string[] solutions, string[]? drivers = null) => new()
        {
            Code = code,
            Name = name,
            Category = category,
            Description = desc,
            Causes = causes,
            Solutions = solutions,
            RelatedDrivers = drivers ?? Array.Empty<string>()
        };

    private static Dictionary<uint, BugCheckEntry> BuildEntries()
    {
        var list = new List<BugCheckEntry>();

        list.Add(E(0x0000000A, "IRQL_NOT_LESS_OR_EQUAL", "驱动程序", "驱动程序在过高的 IRQL 级别上访问了不可分页内存，通常是驱动程序缺陷或内存损坏导致。",
            new[] { "第三方驱动程序存在缺陷", "内存（RAM）物理损坏或接触不良", "驱动程序与系统组件发生内存地址冲突", "BIOS/固件设置不合理（如内存时序）" },
            new[] { "查看崩溃参数 4 对应的驱动模块，更新或回滚该驱动", "运行 Windows 内存诊断 (mdsched.exe)，检查内存故障", "使用 WinDbg 或本工具分析故障模块", "更新 BIOS 至最新版本", "检查是否超频，恢复默认频率" }));

        list.Add(E(0x0000001A, "MEMORY_MANAGEMENT", "内存/系统", "内存管理器检测到严重错误，通常表示内存损坏、驱动错误或文件系统问题。",
            new[] { "物理内存（RAM）故障", "驱动程序对内存的错误操作", "磁盘文件系统错误", "系统文件损坏" },
            new[] { "运行 mdsched.exe 进行内存诊断", "运行 sfc /scannow 修复系统文件", "运行 chkdsk /f 检查磁盘", "更新所有驱动程序", "更换内存条测试" }));

        list.Add(E(0x0000001E, "KMODE_EXCEPTION_NOT_HANDLED", "驱动程序", "内核模式程序产生了处理器无法处理的异常，通常是无效指令或内存访问错误。",
            new[] { "驱动程序缺陷或版本不兼容", "系统服务或内核组件损坏", "磁盘错误导致代码页损坏", "硬件冲突" },
            new[] { "分析转储中的故障模块并更新对应驱动", "运行 sfc /scannow", "检查磁盘错误 chkdsk /f", "卸载最近安装的软件或驱动" }));

        list.Add(E(0x00000024, "NTFS_FILE_SYSTEM", "存储/磁盘", "NTFS 文件系统驱动遇到错误，多与磁盘损坏、磁盘驱动或控制器问题有关。",
            new[] { "硬盘或 SSD 出现坏道/物理损坏", "磁盘控制器或存储驱动缺陷", "断电导致文件系统元数据损坏", "第三方案件与 NTFS 驱动冲突" },
            new[] { "运行 chkdsk /f /r 或其他分区检查", "使用 CrystalDiskInfo 等工具检查磁盘健康状态（SMART）", "更新主板芯片组/存储控制器驱动", "备份重要数据，必要时更换硬盘" }));

        list.Add(E(0x0000003B, "SYSTEM_SERVICE_EXCEPTION", "驱动程序/系统", "系统服务在执行过程中产生异常。在 Win10/11 上常见于驱动缺陷、内存故障或系统更新问题。",
            new[] { "显卡/声卡/网卡等驱动缺陷", "系统更新不完整或损坏", "内存故障", "系统文件损坏" },
            new[] { "分析故障模块，更新对应驱动", "运行 sfc /scannow 和 DISM /Online /Cleanup-Image /RestoreHealth", "卸载最近安装的系统更新", "运行内存诊断" }));

        list.Add(E(0x00000050, "PAGE_FAULT_IN_NONPAGED_AREA", "驱动程序/内存", "系统引用了不可分页区域的无效内存地址。常见于驱动缺陷或内存故障。",
            new[] { "驱动程序使用无效内存指针", "物理内存故障", "磁盘/页面文件问题", "杀毒软件或安全软件冲突" },
            new[] { "检查故障模块，更新/卸载对应驱动", "运行内存诊断 mdsched.exe", "禁用第三方杀毒软件测试", "检查页面文件设置" }));

        list.Add(E(0x0000007B, "INACCESSIBLE_BOOT_DEVICE", "存储/启动", "Windows 在启动过程中无法访问系统分区，通常与磁盘、控制器或启动配置有关。",
            new[] { "硬盘/SSD 连接松动或损坏", "磁盘控制器驱动问题", "MBR/BOOTMGR 损坏", "BIOS 中启动模式（AHCI/IDE）设置改变" },
            new[] { "重启并按 F8 尝试修复模式，运行启动修复", "在修复环境执行 bootrec /rebuildbcd", "检查 BIOS 中 SATA 模式设置", "检查硬盘数据线连接，测试其他接口", "使用 PE 环境备份数据并检查磁盘" }));

        list.Add(E(0x0000007E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "驱动程序/系统", "系统线程产生未处理异常，硬件不兼容或驱动缺陷是常见诱因。",
            new[] { "驱动程序缺陷（尤其显卡驱动）", "硬件不兼容或超频", "内存故障", "系统文件损坏" },
            new[] { "更新或回滚显卡驱动", "恢复 CPU/内存默认频率", "运行内存诊断", "进入安全模式排查第三方驱动" }));

        list.Add(E(0x0000007F, "UNEXPECTED_KERNEL_MODE_TRAP", "硬件/CPU", "内核产生意外陷阱，通常与硬件故障、CPU 超频或散热问题有关。",
            new[] { "CPU 故障或超频不稳定", "散热不良导致过热", "内存故障", "主板供电问题" },
            new[] { "检查散热器与风扇，清理灰尘", "恢复 CPU/内存默认频率", "运行内存与 CPU 压力测试", "更新 BIOS" }));

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
            new[] { "识别故障驱动并更新/卸载", "使用驱动验证器（verifier.exe）定位问题驱动", "更新系统至最新", "运行内存诊断" }));

        list.Add(E(0x000000C4, "DRIVER_VERIFIER_DETECTED_VIOLATION", "驱动程序", "驱动验证器检测到驱动程序违规操作。若自行开启过验证器，说明目标驱动确有问题；否则多为第三方驱动缺陷。",
            new[] { "被验证的驱动程序存在缺陷", "驱动使用了错误的内存分配方式" },
            new[] { "查看验证器报告的违规驱动并替换", "使用 verifier.exe 查询设置，定位后移除问题驱动", "更新驱动至官方最新版本" }));

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
            new[] { "更新或回滚显卡驱动（推荐 DDU 彻底卸载后重装）", "检查 GPU 温度与散热", "恢复 GPU 默认频率", "更新 BIOS 与主板驱动", "若为笔记本，更新 Optimus/混合显卡驱动" }));

        list.Add(E(0x000000EF, "CRITICAL_PROCESS_DIED", "系统/软件", "关键系统进程意外终止，如 csrss、wininit（Win10/11）或 smss 崩溃。",
            new[] { "系统文件损坏", "软件冲突导致关键进程被杀", "存储故障导致系统文件读取失败", "恶意软件" },
            new[] { "运行 sfc /scannow 与 DISM 修复", "检查磁盘健康状态", "检查近期安装的软件并卸载", "全盘查杀病毒", "系统还原至正常时间点" }));

        list.Add(E(0x000000F4, "CRITICAL_OBJECT_TERMINATION", "系统/存储", "系统关键对象（进程/线程）异常终止，常与存储设备或系统服务故障有关。",
            new[] { "系统盘故障或坏道", "驱动/服务崩溃导致关键进程终止", "内存故障", "注册表损坏" },
            new[] { "检查硬盘/SSD 健康状态，更换 SATA/电源线", "运行 chkdsk /f", "分析转储确认终止对象", "更新存储驱动" }));

        list.Add(E(0x000000F7, "DRIVER_OVERRAN_STACK_BUFFER", "驱动程序", "驱动发生栈缓冲区溢出，可能是驱动缺陷或有意的缓冲区溢出攻击。",
            new[] { "驱动程序栈溢出缺陷", "恶意软件利用驱动漏洞" },
            new[] { "更新可疑驱动", "全盘杀毒", "运行 sfc /scannow", "使用驱动验证器定位" }));

        list.Add(E(0x00000101, "CLOCK_WATCHDOG_TIMEOUT", "CPU/硬件", "处理器在时钟间隔内未响应中断。多由超频、电源管理或 CPU 故障引起。",
            new[] { "CPU 超频/降压不稳定", "电源管理（C 状态）问题", "CPU 硬件故障", "主板供电问题" },
            new[] { "恢复 CPU 默认频率与电压", "BIOS 中禁用 C-states 测试", "更新 BIOS/主板驱动", "检查电源供应", "压力测试 CPU（AIDA64/Prime95）" }));

        list.Add(E(0x00000109, "CRITICAL_STRUCTURE_CORRUPTION", "系统/硬件", "内核检测到关键结构损坏，通常被视为内核完整性受影响，与硬件或驱动有关。",
            new[] { "内存故障", "驱动程序破坏内核结构", "超频不稳定", "固件（BIOS/UEFI）缺陷" },
            new[] { "运行内存诊断", "更新或移除可疑驱动", "恢复默认频率", "更新 BIOS", "考虑重装系统排除软件层面因素" }));

        list.Add(E(0x0000010E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "显卡", "视频内存管理器内部错误，几乎总是显卡驱动或显卡硬件问题。",
            new[] { "显卡驱动缺陷", "显存故障", "GPU 过热或超频" },
            new[] { "使用 DDU 彻底卸载并重装最新显卡驱动", "检查 GPU 温度", "恢复 GPU 默认频率", "测试核显/其他显卡排除硬件" }));

        list.Add(E(0x00000113, "VIDEO_DXGKRNL_FATAL_ERROR", "显卡", "DirectX 图形内核驱动 (dxgkrnl) 检测到致命错误。",
            new[] { "显卡驱动缺陷", "GPU 硬件故障", "超频导致不稳定" },
            new[] { "更新/回滚显卡驱动", "降低 GPU 频率测试", "检查电源与 GPU 供电接口" }));

        list.Add(E(0x00000116, "VIDEO_TDR_FAILURE", "显卡", "显示驱动响应超时（TDR），显卡驱动在限制时间内未响应操作系统。这是最常见的显卡相关蓝屏之一。",
            new[] { "显卡驱动崩溃或死锁", "GPU 过热/供电不足", "显卡硬件故障或超频", "驱动程序与游戏/应用不兼容" },
            new[] { "使用 DDU 卸载后安装最新稳定版显卡驱动", "检查 GPU 温度与散热", "恢复 GPU 默认频率", "更新主板 BIOS 与芯片组驱动", "若笔记本，更新混合显卡驱动", "调节 TDR 参数（注册表 TdrLevel/TdrDelay）仅作临时手段" }));

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
            new[] { "更新可疑驱动", "运行内存诊断", "使用驱动验证器" }));

        list.Add(E(0x00000142, "VIDEO_DEVICE_TDR_FAILURE", "显卡", "视频设备 TDR 故障，常见于显卡硬件或驱动。",
            new[] { "显卡驱动崩溃", "GPU 硬件故障", "显卡超频/过热" },
            new[] { "DDU 重装显卡驱动", "检查散热", "恢复默认频率" }));

        list.Add(E(0x00000154, "UNEXPECTED_STORE_EXCEPTION", "存储/SSD", "存储组件（Store）产生意外异常，通常与存储设备、存储驱动或 RAID 配置有关。",
            new[] { "SSD/存储设备故障", "存储驱动缺陷", "固件或磁盘加密组件问题", "内存故障" },
            new[] { "检查 SSD 健康状态与固件更新", "更新存储与芯片组驱动", "运行内存诊断", "运行 chkdsk /f" }));

        list.Add(E(0x00000158, "ECC_HARDWARE_CORRUPTION", "硬件/ECC", "支持 ECC 的内存报告了硬件级数据损坏。",
            new[] { "ECC 内存硬件故障", "内存接触不良" },
            new[] { "检查内存条与插槽", "更新 BIOS", "更换内存" }));

        list.Add(E(0x0000021A, "WIN32K_CRITICAL_FAILURE", "系统/图形", "Win32k 子系统发生致命错误，可能与图形驱动或其他内核组件冲突有关。",
            new[] { "系统文件损坏", "视频驱动问题", "第三方软件注入 win32k" },
            new[] { "运行 sfc /scannow 与 DISM", "更新显卡驱动", "进入安全模式排查第三方软件" }));

        list.Add(E(0x0000021B, "EXFAT_FILE_SYSTEM", "存储", "exFAT 文件系统驱动遇到错误，多与外接存储设备或存储驱动有关。",
            new[] { "U 盘/移动硬盘损坏（exFAT 格式）", "存储控制器驱动问题", "异常断电导致卷损坏" },
            new[] { "运行 chkdsk /f 检查对应卷", "检查外接存储设备健康", "更新存储驱动" }));

        list.Add(E(0x00000076, "PROCESS_HAS_LOCKED_PAGES", "驱动程序", "驱动在处理 I/O 后未正确解锁内存页。",
            new[] { "驱动未释放锁定的内存页", "驱动缺陷" },
            new[] { "更新对应驱动", "检查转储中的故障模块" }));

        list.Add(E(0x00000077, "KERNEL_STACK_INPAGE_ERROR", "存储/页面文件", "系统无法从页面文件或磁盘读取内核栈数据，多为磁盘故障。",
            new[] { "硬盘/SSD 坏道或故障", "页面文件所在磁盘出现问题", "磁盘控制器驱动错误" },
            new[] { "运行 chkdsk /f /r", "检查磁盘 SMART 状态", "备份数据，更换磁盘", "更新磁盘控制器驱动" }));

        list.Add(E(0x000000B4, "VIDEO_DRIVER_INIT_FAILURE", "显卡驱动", "显卡驱动初始化失败，常因驱动损坏或显卡硬件问题。",
            new[] { "显卡驱动损坏或不兼容", "显卡硬件故障", "注册表驱动信息损坏" },
            new[] { "进入安全模式，DDU 卸载显卡驱动后重装", "检查显卡供电与插槽", "更新 BIOS" }));

        list.Add(E(0x000000BE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", "驱动程序", "驱动尝试写入只读内存。",
            new[] { "驱动内存属性错误", "驱动缺陷" },
            new[] { "更新/卸载故障驱动", "运行内存诊断" }));

        list.Add(E(0x000000DE, "POOL_CORRUPTION_IN_FILE_AREA", "驱动程序", "文件系统区域中的内存池被破坏。",
            new[] { "驱动内存越界", "磁盘错误", "内存故障" },
            new[] { "更新故障驱动", "运行 chkdsk /f", "运行内存诊断" }));

        list.Add(E(0x00000080, "NMI_HARDWARE_FAILURE", "硬件", "系统收到不可屏蔽中断（NMI），表示硬件电路故障。",
            new[] { "内存/主板硬件故障", "电源故障", "固件问题" },
            new[] { "检查内存与插槽", "更新 BIOS", "检查电源", "送修硬件" }));

        list.Add(E(0x000000E3, "RESOURCE_NOT_OWNED", "驱动程序", "驱动试图释放不拥有的资源。",
            new[] { "驱动资源管理错误", "驱动缺陷" },
            new[] { "更新/卸载相关驱动", "使用驱动验证器定位" }));

        list.Add(E(0x000000F1, "SCSI_VERIFIER_DETECTED_VIOLATION", "驱动/存储", "SCSI 驱动验证器检测到违规，与存储类驱动的内存操作有关。",
            new[] { "存储驱动缺陷", "磁盘控制器驱动问题" },
            new[] { "更新存储/芯片组驱动", "检查磁盘健康" }));

        list.Add(E(0x0000010F, "RESOURCE_MANAGER_EXCEPTION_NOT_HANDLED", "系统", "资源管理器异常未处理，多与内核资源问题相关。",
            new[] { "资源许可/句柄管理错误", "驱动问题" },
            new[] { "更新系统与驱动", "运行 sfc /scannow" }));

        list.Add(E(0x00000119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "显卡", "视频调度器内部错误，与 GPU 驱动或硬件有关。",
            new[] { "显卡驱动缺陷", "GPU 硬件故障或显存问题", "超频" },
            new[] { "DDU 重装显卡驱动", "恢复默认频率", "检查 GPU 温度与供电" }));

        list.Add(E(0x0000014F, "SHADOW_STACK_VIOLATION", "系统/CPU", "硬件强制栈保护（CET Shadow Stack）被违反，可能表示驱动/软件篡改返回地址。",
            new[] { "不兼容的驱动或软件", "内存损坏", "恶意软件" },
            new[] { "更新所有驱动与软件", "运行 sfc /scannow", "全盘杀毒" }));

        var dict = new Dictionary<uint, BugCheckEntry>();
        foreach (var e in list) dict[e.Code] = e;
        return dict;
    }
}