using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickPeek;

public sealed class ActionConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sys")] public string Sys { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "popup";
}

public sealed class AppRule
{
    [JsonPropertyName("match")] public string Match { get; set; } = "";
    [JsonPropertyName("keys")] public string Keys { get; set; } = "";
}

/// <summary>
/// 与 AHK 版 settings.json 完全同构，可直接互相读取。
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("api_base")] public string ApiBase { get; set; } = "http://127.0.0.1:8080/v1";
    [JsonPropertyName("api_key")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "LFM2.5-24B-A2B-QAD-Q4_0.gguf";
    [JsonPropertyName("timeout_sec")] public int TimeoutSec { get; set; } = 180;
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.3;
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 2000;
    [JsonPropertyName("strip_reasoning")] public bool StripReasoning { get; set; } = true;
    [JsonPropertyName("mouse_trigger")] public bool MouseTrigger { get; set; } = true;
    [JsonPropertyName("auto_hide_sec")] public int AutoHideSec { get; set; } = 6;
    [JsonPropertyName("remember_pos")] public bool RememberPos { get; set; }
    [JsonPropertyName("render_md")] public bool RenderMarkdown { get; set; } = true;
    [JsonPropertyName("hk_peek")] public string HkPeek { get; set; } = "Alt+Q";
    [JsonPropertyName("hk_clip")] public string HkClip { get; set; } = "Ctrl+Alt+L";
    [JsonPropertyName("copy_keys")] public string CopyKeys { get; set; } = "Ctrl+C";
    [JsonPropertyName("app_copy_keys")] public List<AppRule> AppCopyKeys { get; set; } = new();
    [JsonPropertyName("actions")] public List<ActionConfig> Actions { get; set; } = new();

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(
        JsonSerializer.Serialize(this, ConfigStore.JsonOpts), ConfigStore.JsonOpts)!;

    public static AppConfig Default() => new()
    {
        AppCopyKeys =
        {
            new AppRule { Match = @"WindowsTerminal|wt\.exe|cmd\.exe|pwsh|conhost", Keys = "Ctrl+Shift+C" }
        },
        Actions =
        {
            new ActionConfig
            {
                Name = "翻译",
                Sys = "你是专业翻译。把用户内容翻译成简体中文；代码、命令、专有名词保留原文；直接输出译文，不要解释，不要展示思考过程。",
                Mode = "popup"
            },
            new ActionConfig
            {
                Name = "分析日志",
                Sys = "你是资深运维工程师。分析用户给的日志片段，指出错误原因和修复建议。用中文，结论先行，条目化输出，不要废话。",
                Mode = "popup"
            }
        }
    };
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static string? _dir;

    /// <summary>配置文件目录：优先 exe 同目录（与 AHK 版一致），不可写则退到 %APPDATA%\QuickPeek。</summary>
    public static string Dir
    {
        get
        {
            if (_dir != null) return _dir;
            var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
            try
            {
                var probe = Path.Combine(exeDir, ".write_probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                _dir = exeDir;
            }
            catch
            {
                var appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPeek");
                Directory.CreateDirectory(appData);
                _dir = appData;
            }
            return _dir;
        }
    }

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var def = AppConfig.Default();
                Save(def);
                Log.Write("首次运行：已生成默认配置");
                return def;
            }

            var json = File.ReadAllText(FilePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
            if (cfg == null) throw new JsonException("反序列化结果为空");

            // 补齐缺失字段（旧配置文件可能没有新增项）
            var d = AppConfig.Default();
            if (cfg.Actions.Count == 0) cfg.Actions = d.Actions;
            if (string.IsNullOrWhiteSpace(cfg.ApiBase)) cfg.ApiBase = d.ApiBase;
            if (string.IsNullOrWhiteSpace(cfg.CopyKeys)) cfg.CopyKeys = d.CopyKeys;
            if (string.IsNullOrWhiteSpace(cfg.HkPeek)) cfg.HkPeek = d.HkPeek;
            if (string.IsNullOrWhiteSpace(cfg.HkClip)) cfg.HkClip = d.HkClip;
            if (cfg.TimeoutSec <= 0) cfg.TimeoutSec = d.TimeoutSec;
            if (cfg.AutoHideSec <= 0) cfg.AutoHideSec = d.AutoHideSec;
            foreach (var a in cfg.Actions)
                if (string.IsNullOrWhiteSpace(a.Mode)) a.Mode = "popup";

            return cfg;
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 读取 settings.json 失败: {ex.Message}");
            return AppConfig.Default();
        }
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            var json = JsonSerializer.Serialize(cfg, JsonOpts);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 保存 settings.json 失败: {ex.Message}");
        }
    }
}
