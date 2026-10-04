using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfOcrRenamer
{
    public static class AppConfig
    {
        public const string ConfigFileName = "config.json";

        public static readonly Dictionary<string, object> Defaults = new Dictionary<string, object>
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
            if (exeDir != null)
            {
                var probe = new DirectoryInfo(exeDir);
                for (var i = 0; i < 4 && probe != null; i++)
                {
                    if (File.Exists(Path.Combine(probe.FullName, "PdfOcrRenamer.sln"))) return probe.FullName;
                    probe = probe.Parent;
                }
            }
            return exeDir;
        }

        public static string ConfigPath() { return Path.Combine(ConfigDir(), ConfigFileName); }

        private static Dictionary<string, object> Normalize(JsonNode node)
        {
            var result = new Dictionary<string, object>();
            if (node is JsonObject)
            {
                foreach (var kv in (JsonObject)node)
                {
                    if (kv.Value is JsonArray)
                    {
                        var list = new List<string>();
                        foreach (var n in (JsonArray)kv.Value)
                        {
                            var val = n == null ? null : (string)n;
                            if (val != null) list.Add(val);
                        }
                        result[kv.Key] = list;
                    }
                    else
                    {
                        result[kv.Key] = kv.Value;
                    }
                }
            }
            return result;
        }

        public static Dictionary<string, object> LoadConfig()
        {
            var path = ConfigPath();
            var cfg = new Dictionary<string, object>(Defaults);
            if (File.Exists(path))
            {
                try
                {
                    var parsed = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (parsed is JsonObject) cfg = Normalize(parsed);
                }
                catch { }
            }
            else
            {
                SaveConfig(cfg);
            }
            return cfg;
        }

        public static void SaveConfig(Dictionary<string, object> cfg)
        {
            var path = ConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var merged = new Dictionary<string, object>(Defaults);
            foreach (var kv in cfg) merged[kv.Key] = kv.Value;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(merged, ConfigJson.Options));
            File.Delete(path);
            File.Move(tmp, path);
        }

        public static string GetString(this Dictionary<string, object> cfg, string key, string fallback = "")
        {
            object v;
            if (!cfg.TryGetValue(key, out v) || v == null) return fallback;
            if (v is JsonValue)
            {
                string s;
                if (((JsonValue)v).TryGetValue(out s)) return s;
            }
            if (v is string) return (string)v;
            return fallback;
        }

        public static double GetDouble(this Dictionary<string, object> cfg, string key, double fallback)
        {
            object v;
            if (!cfg.TryGetValue(key, out v) || v == null) return fallback;
            if (v is JsonValue)
            {
                double d;
                if (((JsonValue)v).TryGetValue(out d)) return d;
            }
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            return fallback;
        }

        public static int GetInt(this Dictionary<string, object> cfg, string key, int fallback)
        {
            object v;
            if (!cfg.TryGetValue(key, out v) || v == null) return fallback;
            if (v is JsonValue)
            {
                int i;
                if (((JsonValue)v).TryGetValue(out i)) return i;
            }
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)(double)v;
            return fallback;
        }

        public static bool GetBool(this Dictionary<string, object> cfg, string key, bool fallback)
        {
            object v;
            if (!cfg.TryGetValue(key, out v) || v == null) return fallback;
            if (v is JsonValue)
            {
                bool b;
                if (((JsonValue)v).TryGetValue(out b)) return b;
            }
            if (v is bool) return (bool)v;
            return fallback;
        }

        public static List<string> GetList(this Dictionary<string, object> cfg, string key)
        {
            object v;
            if (!cfg.TryGetValue(key, out v) || v == null) return new List<string>();
            if (v is List<string>) return new List<string>((List<string>)v);
            if (v is List<object>)
            {
                var res = new List<string>();
                foreach (var o in (List<object>)v) if (o is string) res.Add((string)o);
                return res;
            }
            if (v is JsonArray)
            {
                var res = new List<string>();
                foreach (var n in (JsonArray)v) if (n != null) res.Add((string)n);
                return res;
            }
            return new List<string>();
        }

        public static string GetApiKey(Dictionary<string, object> cfg)
        {
            var key = cfg.GetString("ionos_api_key");
            if (key.Length > 0) return key;
            return Environment.GetEnvironmentVariable("IONOS_API_TOKEN") ?? "";
        }
    }

    public static class ConfigJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
