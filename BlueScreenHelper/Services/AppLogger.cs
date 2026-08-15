using System.IO;
using System.Reflection;
using Microsoft.UI.Xaml;

namespace BlueScreenHelper.Services;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _dir;

    public static string LogDir
    {
        get
        {
            _dir ??= ResolveLogDir();
            return _dir;
        }
    }

    public static void Init()
    {
        var _ = LogDir;
        CleanupOldLogs();
        Application.Current.UnhandledException += (_, e) =>
        {
            LogError("UI 线程未处理异常", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogError("AppDomain 未处理异常", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError("后台任务未观察异常", e.Exception);
            e.SetObserved();
        };
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        LogInfo($"应用启动 v{ver} OS={Environment.OSVersion.Version} " +
                $"{(Environment.Is64BitOperatingSystem ? "x64" : "x86")} 日志目录={LogDir}");
    }

    public static void LogInfo(string message) => Write("INFO", message);

    public static void LogError(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : message + " | " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                var path = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static string ResolveLogDir()
    {
        var exeDir = Path.Combine(AppContext.BaseDirectory, "log");
        try
        {
            Directory.CreateDirectory(exeDir);
            var probe = Path.Combine(exeDir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return exeDir;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception)
        {
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueScreenHelper", "logs");
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
        }
        return fallback;
    }

    private static void CleanupOldLogs()
    {
        try
        {
            foreach (var file in Directory.GetFiles(LogDir, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-30))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
        }
    }
}
