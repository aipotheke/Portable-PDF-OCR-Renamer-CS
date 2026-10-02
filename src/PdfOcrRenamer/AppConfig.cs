using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfOcrRenamer;

public static class AppConfig
{
    public const string ConfigFileName = "config.json";

    public static readonly Dictionary<string, object?> Defaults = new()
    {
        ["ionos_api_key"] = "",
        ["ionos_base_url"] = "https://openai.inference.de-txl.ionos.com/v1",
        ["ocr_model"] = "lightonai/LightOnOCR-2-1B",
        ["classify_model"] = "mistralai/Mistral-Small-24B-Instruct",
        ["watch_folder"] = "",
        ["doc_types"] = new List<string> { "invoice", "letter", "receipt", "contract", "other" },
        ["render_scale"] = 2.0,
        ["ocr_max_tokens"] = 4096,
        ["ocr_temperature"] = 0.2,
        ["request_timeout"] = 120,
        ["max_retries"] = 4,
        ["keep_md_sidecar"] = true,
        ["sender_in_filename"] = true,
        ["stability_seconds"] = 3.0,
        ["stability_max_wait"] = 120.0,
    };

    public static string ConfigDir()
    {
        var exeDir = AppContext.BaseDirectory;
        return new DirectoryInfo(exeDir).Parent?.Parent?.Parent?.Parent?.FullName is { } repo && File.Exists(Path.Combine(repo, "PdfOcrRenamer.sln"))
            ? repo
            : exeDir;
    }

    public static string ConfigPath() => Path.Combine(ConfigDir(), ConfigFileName);

    private static Dictionary<string, object?> Normalize(JsonNode? node)
    {
        var result = new Dictionary<string, object?>();
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                result[kv.Key] = kv.Value is JsonArray arr
                    ? arr.OfType<JsonNode>().Select(n => (string?)n).OfType<string>().Cast<object?>().ToList()
                    : (object?)kv.Value;
            }
        }
        return result;
    }

    public static Dictionary<string, object?> LoadConfig()
    {
        var path = ConfigPath();
        var cfg = new Dictionary<string, object?>(Defaults);
        if (File.Exists(path))
        {
            try
            {
                cfg = Normalize(JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)));
            }
            catch
            {
            }
        }
        else
        {
            SaveConfig(cfg);
        }
        return cfg;
    }

    public static void SaveConfig(Dictionary<string, object?> cfg)
    {
        var path = ConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var merged = new Dictionary<string, object?>(Defaults);
        foreach (var kv in cfg) merged[kv.Key] = kv.Value;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(merged, ConfigJson.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static string GetString(this Dictionary<string, object?> cfg, string key, string fallback = "")
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        if (v is string str) return str;
        return fallback;
    }

    public static double GetDouble(this Dictionary<string, object?> cfg, string key, double fallback)
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonValue jvd && jvd.TryGetValue<double>(out var d)) return d;
        if (v is double dd) return dd;
        if (v is int di) return di;
        if (v is long dl) return dl;
        return fallback;
    }

    public static int GetInt(this Dictionary<string, object?> cfg, string key, int fallback)
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonValue jvi && jvi.TryGetValue<int>(out var i)) return i;
        if (v is int ii) return ii;
        if (v is long il) return (int)il;
        if (v is double id) return (int)id;
        return fallback;
    }

    public static bool GetBool(this Dictionary<string, object?> cfg, string key, bool fallback)
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is JsonValue jvb && jvb.TryGetValue<bool>(out var b)) return b;
        if (v is bool bb) return bb;
        return fallback;
    }

    public static List<string> GetList(this Dictionary<string, object?> cfg, string key)
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return new List<string>();
        if (v is List<object?> l) return l.OfType<string>().ToList();
        if (v is List<string> ls) return new List<string>(ls);
        if (v is JsonArray arr) return arr.Select(n => (string?)n).OfType<string>().ToList();
        return new List<string>();
    }

    public static string GetApiKey(Dictionary<string, object?> cfg)
    {
        var key = cfg.GetString("ionos_api_key");
        return key.Length > 0 ? key : Environment.GetEnvironmentVariable("IONOS_API_TOKEN") ?? "";
    }
}

public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
