using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BlueScreenHelper.Models;

namespace BlueScreenHelper.Services;

public sealed class AIService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string> ChatAsync(AppSettings settings, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("尚未配置 API Key，请先在“设置”页面填写。");
        }

        var messages = new List<object>();
        var sys = string.IsNullOrWhiteSpace(systemPromptOverride) ? settings.SystemPrompt : systemPromptOverride;
        if (!string.IsNullOrWhiteSpace(sys))
        {
            messages.Add(new { role = "system", content = sys });
        }
        foreach (var m in history)
        {
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                messages.Add(new { role = m.Role, content = m.Content });
            }
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o-mini" : settings.Model,
            messages,
            stream = true,
            temperature = settings.Temperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(settings.ApiBaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"API 请求失败 ({(int)response.StatusCode})：{Truncate(err, 400)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }
            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                {
                    var text = contentProp.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text);
                        onDelta?.Invoke(text);
                    }
                }
            }
            catch
            {
            }
        }

        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("API 未返回有效内容，请检查模型名称、接口地址或 API Key。");
        }
        return result;
    }

    public async Task<string> TestConnectionAsync(AppSettings settings)
    {
        var history = new List<ChatMessage>
        {
            new() { Role = "user", Content = "请仅回复两个字符：OK" }
        };
        var overridePrompt = "你是一个连接测试助手，收到任何消息只回复 OK。";
        return await ChatAsync(settings, history, overridePrompt, null, CancellationToken.None);
    }

    private static Uri BuildUri(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0)
        {
            b = "https://api.openai.com/v1";
        }
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(b);
        }
        return new Uri(b + "/chat/completions");
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max] + "...";
    }
}