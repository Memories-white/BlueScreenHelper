using System.Globalization;
using System.Text.RegularExpressions;

namespace BlueScreenHelper.Services;

internal static partial class SystemEventParser
{
    public static bool TryParseBugCheck(string description, out uint code, out string? driver)
    {
        code = 0;
        driver = null;
        var codeMatch = BugCheckCodeRegex().Match(description);
        if (!codeMatch.Success ||
            !uint.TryParse(codeMatch.Groups[1].ValueSpan, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out code))
        {
            return false;
        }

        var driverMatch = DriverRegex().Match(description);
        driver = driverMatch.Success ? driverMatch.Groups[1].Value : null;
        return true;
    }

    public static string ExtractApplicationName(string description)
    {
        var match = ApplicationNameRegex().Match(description);
        return match.Success ? match.Groups[1].Value : "";
    }

    [GeneratedRegex(@"0x([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex BugCheckCodeRegex();

    [GeneratedRegex(@"([a-zA-Z0-9_-]+\.sys)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DriverRegex();

    [GeneratedRegex(@"(?:应用程序名称|faulting application name):\s*([^\s,]+\.exe)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationNameRegex();
}
