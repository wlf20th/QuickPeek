using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuickPeek;

public readonly record struct LlmResult(string Content, string Reasoning, long ElapsedMs);

/// <summary>
/// OpenAI 兼容 /chat/completions 客户端。
/// 关键差异：AHK 版走 curl 子进程 + 临时文件 + 300ms 轮询，
/// 这里是原生异步 HttpClient，无临时文件、无轮询、无看门狗。
/// </summary>
internal sealed class LlmClient : IDisposable
{
    private HttpClient _http;
    private AppConfig _cfg;

    public LlmClient(AppConfig cfg)
    {
        _cfg = cfg;
        _http = Build(cfg);
    }

    private static HttpClient Build(AppConfig cfg)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,                      // 等价 curl --noproxy "*"
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, cfg.TimeoutSec))
        };
    }

    public void Reconfigure(AppConfig cfg)
    {
        _cfg = cfg;
        var old = _http;
        _http = Build(cfg);
        old.Dispose();
    }

    private string Endpoint =>
        _cfg.ApiBase.TrimEnd('/') + "/chat/completions";

    public async Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var body = new
        {
            model = _cfg.Model,
            stream = false,
            temperature = _cfg.Temperature,
            max_tokens = _cfg.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        req.Content = new StringContent(
            JsonSerializer.Serialize(body), new UTF8Encoding(false), "application/json");
        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            var snippet = json.Length > 400 ? json[..400] : json;
            throw new HttpRequestException(
                $"服务返回 {(int)resp.StatusCode} {resp.ReasonPhrase}\n{snippet}");
        }

        return Parse(json, sw.ElapsedMilliseconds);
    }

    private LlmResult Parse(string json, long elapsed)
    {
        string content = "", reasoning = "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
                throw new InvalidOperationException("服务端错误: " + msg);
            }

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty("message", out var message))
                {
                    // content 可能是字符串，也可能是分段数组
                    if (message.TryGetProperty("content", out var ct1))
                    {
                        content = ct1.ValueKind switch
                        {
                            JsonValueKind.String => ct1.GetString() ?? "",
                            JsonValueKind.Array => string.Concat(
                                ct1.EnumerateArray().Select(e =>
                                    e.TryGetProperty("text", out var t) ? t.GetString() : "")),
                            _ => ""
                        };
                    }
                    if (message.TryGetProperty("reasoning_content", out var rc)
                        && rc.ValueKind == JsonValueKind.String)
                        reasoning = rc.GetString() ?? "";
                }
                else if (c0.TryGetProperty("text", out var txt)) // 旧版 completions
                {
                    content = txt.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应：原样兜底，方便排查
            return new LlmResult(
                "⚠️ 响应不是合法 JSON，原文前 800 字：\n\n" +
                (json.Length > 800 ? json[..800] : json), "", elapsed);
        }

        if (content.Contains("<think", StringComparison.OrdinalIgnoreCase))
            content = Regex.Replace(content, "<think[^>]*>.*?</think\\s*>\\s*", "",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (content.Trim().Length == 0 && reasoning.Trim().Length > 0)
            content = "💡 [模型只输出了思考过程，供参考]\n\n" + reasoning;
        else if (!_cfg.StripReasoning && reasoning.Trim().Length > 0)
            content = "🤔 思考过程：\n" + reasoning + "\n\n—— 答案 ——\n" + content;

        if (content.Trim().Length == 0)
            content = "⚠️ 未解析到回答，原始响应：\n\n" + (json.Length > 800 ? json[..800] : json);

        return new LlmResult(content.Trim(), reasoning.Trim(), elapsed);
    }

    public void Dispose() => _http.Dispose();
}
