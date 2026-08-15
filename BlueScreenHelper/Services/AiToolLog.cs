using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;

namespace BlueScreenHelper.Services;

public sealed class AiToolLogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string ToolName { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Success { get; set; }
    public string ResultPreview { get; set; } = "";

    public string TimeText => Time.ToString("HH:mm:ss");
    public string StatusText => Success ? "成功" : "失败";
    public string StatusColor => Success ? "#4CAF50" : "#E81123";
}

public static class AiToolLog
{
    public static ObservableCollection<AiToolLogEntry> Entries { get; } = new();

    public static void Add(string toolName, string action, bool success, string resultPreview)
    {
        AppLogger.LogInfo($"AI工具调用: [{toolName}] {action} {(success ? "成功" : "失败")}");
        var queue = App.MainWindow?.DispatcherQueue;
        var entry = new AiToolLogEntry
        {
            ToolName = toolName,
            Action = action,
            Success = success,
            ResultPreview = string.IsNullOrEmpty(resultPreview)
                ? (success ? "已获取数据" : "无返回结果")
                : (resultPreview.Length > 140 ? resultPreview[..140] + "..." : resultPreview)
        };
        if (queue != null)
        {
            queue.TryEnqueue(() => Insert(entry));
        }
        else
        {
            Insert(entry);
        }
    }

    private static void Insert(AiToolLogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > 200)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
    }
}
