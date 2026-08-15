using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace BlueScreenHelper.Services;

public sealed class UpdateInfo
{
    public Version LatestVersion { get; set; } = new(0, 0, 0);
    public string InstallerUrl { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
}

public static class UpdaterService
{
    public const string RepoOwner = "Memories-white";
    public const string RepoName = "BlueScreenHelper";

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var client = CreateClient();

        var (tag, releaseUrl) = await GetLatestTagAsync(client);
        if (string.IsNullOrEmpty(tag))
        {
            throw new InvalidOperationException(
                "获取最新版本失败，请确认网络可访问 GitHub。若需走代理，请设置 HTTPS_PROXY 环境变量后重启应用。");
        }
        if (!tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(tag[1..].Split('-')[0], out var latest))
        {
            throw new InvalidOperationException($"无法解析远程版本号：{tag}");
        }

        var verStr = $"{latest.Major}.{latest.Minor}.{latest.Build}";
        var installerUrl =
            $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tag}/BlueScreenHelper_Setup_{verStr}_win-x64.exe";

        var notes = await GetReleaseNotesAsync(client);

        return new UpdateInfo
        {
            LatestVersion = latest,
            InstallerUrl = installerUrl,
            ReleaseUrl = string.IsNullOrEmpty(releaseUrl)
                ? $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/{tag}"
                : releaseUrl,
            ReleaseNotes = notes
        };
    }

    public static async Task<string> DownloadAsync(string url, IProgress<double>? progress)
    {
        using var client = CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);

        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        var target = Path.Combine(Path.GetTempPath(), fileName);

        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"下载安装包失败 ({(int)resp.StatusCode})。");
        }

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            readTotal += read;
            if (total > 0)
            {
                progress?.Report((double)readTotal / total);
            }
        }
        return target;
    }

    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        });
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = true };
        var proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                    ?? Environment.GetEnvironmentVariable("https_proxy")
                    ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                    ?? Environment.GetEnvironmentVariable("http_proxy");
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy.Trim().Trim('"'));
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "BlueScreenHelper-Updater");
        return client;
    }

    private static async Task<(string? Tag, string? Url)> GetLatestTagAsync(HttpClient client)
    {
        try
        {
            using var resp = await client.GetAsync(
                $"https://github.com/{RepoOwner}/{RepoName}/releases/latest").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var final = resp.RequestMessage?.RequestUri?.ToString();
                const string marker = "/releases/tag/";
                var idx = final?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
                if (idx >= 0 && final != null)
                {
                    return (final[(idx + marker.Length)..], final);
                }
            }
        }
        catch
        {
        }

        try
        {
            using var resp = await client.GetAsync(
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(tag))
                {
                    return (tag, url);
                }
            }
        }
        catch
        {
        }

        return (null, null);
    }

    private static async Task<string> GetReleaseNotesAsync(HttpClient client)
    {
        try
        {
            using var resp = await client.GetAsync(
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return "";
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }
}
