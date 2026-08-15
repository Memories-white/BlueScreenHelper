namespace BlueScreenHelper.Models;

public sealed class BugCheckEntry
{
    public uint Code { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Causes { get; set; } = Array.Empty<string>();
    public string[] Solutions { get; set; } = Array.Empty<string>();
    public string[] RelatedDrivers { get; set; } = Array.Empty<string>();

    public string CodeHex => $"0x{Code:X8}";
}