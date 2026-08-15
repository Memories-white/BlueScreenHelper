using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BlueScreenHelper.Models;

namespace BlueScreenHelper.Services;

public sealed class AIService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string> ChatAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        if (config == null)
        {
            throw new InvalidOperationException("尚未选择 AI 配置，请先在“设置”页面保存一个 AI 服务。");
        }
        if (string.IsNullOrWhiteSpace(config.ApiKey) && config.Provider != AIProvider.Custom)
        {
            throw new InvalidOperationException("该 AI 配置尚未填写 API Key，请先在“设置”页面填写。");
        }

        return config.Provider switch
        {
            AIProvider.Anthropic => await ChatAnthropicAsync(config, history, systemPromptOverride, onDelta, ct),
            AIProvider.Gemini => await ChatGeminiAsync(config, history, systemPromptOverride, onDelta, ct),
            _ => await ChatOpenAIAsync(config, history, systemPromptOverride, onDelta, ct)
        };
    }

    public async Task<string> TestConnectionAsync(AIConfigItem config)
    {
        var history = new List<ChatMessage>
        {
            new() { Role = "user", Content = "请仅回复两个字符：OK" }
        };
        return await ChatAsync(config, history, "你是一个连接测试助手，收到任何消息只回复 OK。", null, CancellationToken.None);
    }

    // ---------- OpenAI 兼容协议（OpenAI / Custom / Ollama / DeepSeek 等） ----------

    private async Task<string> ChatOpenAIAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        var messages = new List<object>();
        var sys = string.IsNullOrWhiteSpace(systemPromptOverride) ? config.SystemPrompt : systemPromptOverride;
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
            model = string.IsNullOrWhiteSpace(config.Model) ? "gpt-4o-mini" : config.Model,
            messages,
            stream = true,
            temperature = config.Temperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildOpenAIUri(config.ApiBaseUrl));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
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
                    AppendDelta(sb, contentProp.GetString(), onDelta);
                }
            }
            catch
            {
            }
        }

        return FinishResult(sb);
    }

    // ---------- Anthropic 协议 ----------

    private async Task<string> ChatAnthropicAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        var sys = string.IsNullOrWhiteSpace(systemPromptOverride) ? config.SystemPrompt : systemPromptOverride;
        var messages = new List<object>();
        foreach (var m in history)
        {
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
            }
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(config.Model) ? "claude-sonnet-4-5" : config.Model,
            max_tokens = 4096,
            system = sys ?? "",
            messages,
            stream = true,
            temperature = config.Temperature
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAnthropicUri(config.ApiBaseUrl));
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
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
                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                {
                    if (typeProp.GetString() == "content_block_delta" &&
                        doc.RootElement.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("type", out var deltaType) &&
                        deltaType.GetString() == "text_delta" &&
                        delta.TryGetProperty("text", out var textProp))
                    {
                        AppendDelta(sb, textProp.GetString(), onDelta);
                    }
                }
            }
            catch
            {
            }
        }

        return FinishResult(sb);
    }

    // ---------- Gemini 协议 ----------

    private async Task<string> ChatGeminiAsync(AIConfigItem config, IReadOnlyList<ChatMessage> history,
        string? systemPromptOverride, Action<string>? onDelta, CancellationToken ct)
    {
        var sys = string.IsNullOrWhiteSpace(systemPromptOverride) ? config.SystemPrompt : systemPromptOverride;
        var contents = new List<object>();
        foreach (var m in history)
        {
            if (!string.IsNullOrWhiteSpace(m.Content))
            {
                contents.Add(new { role = m.Role == "assistant" ? "model" : "user", parts = new[] { new { text = m.Content } } });
            }
        }

        var body = new
        {
            contents,
            systemInstruction = string.IsNullOrWhiteSpace(sys) ? null : new { parts = new[] { new { text = sys } } },
            generationConfig = new { temperature = config.Temperature }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildGeminiUri(config));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Add("x-goog-api-key", config.ApiKey);
        }
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
            try
            {
                using var doc = JsonDocument.Parse(data);
                var arr = doc.RootElement;
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var first = arr[0];
                    if (first.TryGetProperty("candidates", out var candidates) &&
                        candidates.GetArrayLength() > 0 &&
                        candidates[0].TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var textProp))
                    {
                        AppendDelta(sb, textProp.GetString(), onDelta);
                    }
                }
            }
            catch
            {
            }
        }

        return FinishResult(sb);
    }

    // ---------- 辅助 ----------

    private static void AppendDelta(StringBuilder sb, string? text, Action<string>? onDelta)
    {
        if (!string.IsNullOrEmpty(text))
        {
            sb.Append(text);
            onDelta?.Invoke(text);
        }
    }

    private static string FinishResult(StringBuilder sb)
    {
        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("API 未返回有效内容，请检查模型名称、接口地址或 API Key。");
        }
        return result;
    }

    private static Uri BuildOpenAIUri(string baseUrl)
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

    private static Uri BuildAnthropicUri(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0)
        {
            b = "https://api.anthropic.com/v1";
        }
        if (b.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(b);
        }
        return new Uri(b + "/messages");
    }

    private static Uri BuildGeminiUri(AIConfigItem config)
    {
        var b = (config.ApiBaseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0)
        {
            b = "https://generativelanguage.googleapis.com/v1beta";
        }
        var model = string.IsNullOrWhiteSpace(config.Model) ? "gemini-2.0-flash" : config.Model;
        return new Uri($"{b}/models/{model}:streamGenerateContent?alt=sse");
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..max] + "...";
    }
}
