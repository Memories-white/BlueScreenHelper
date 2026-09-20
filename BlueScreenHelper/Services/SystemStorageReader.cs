using System.Management;

namespace BlueScreenHelper.Services;

internal readonly record struct LogicalDiskInfo(string Id, ulong TotalBytes, ulong FreeBytes)
{
    public double UsedPercent => (double)(TotalBytes - FreeBytes) / TotalBytes * 100.0;
}

internal sealed record SmartSnapshot(int DeviceCount, IReadOnlyList<string> FailedDevices);

internal static class SystemStorageReader
{
    public static List<LogicalDiskInfo> GetLogicalDisks()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
        using var results = searcher.Get();
        var disks = new List<LogicalDiskInfo>(results.Count);

        foreach (var item in results)
        {
            using var disk = (ManagementObject)item;
            var total = Convert.ToUInt64(disk["Size"] ?? 0UL);
            if (total == 0)
            {
                continue;
            }

            disks.Add(new LogicalDiskInfo(
                Convert.ToString(disk["DeviceID"]) ?? "",
                total,
                Convert.ToUInt64(disk["FreeSpace"] ?? 0UL)));
        }

        return disks;
    }

    public static SmartSnapshot GetSmartSnapshot()
    {
        var scope = new ManagementScope(@"\\.\root\WMI");
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"));
        using var results = searcher.Get();
        var failed = new List<string>();
        var deviceCount = 0;

        foreach (var item in results)
        {
            using var disk = (ManagementObject)item;
            deviceCount++;
            if (disk["PredictFailure"] is bool predict && predict)
            {
                failed.Add(Convert.ToString(disk["InstanceName"]) ?? "未知磁盘");
            }
        }

        return new SmartSnapshot(deviceCount, failed);
    }
}
