using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfOcrRenamer
{
    public readonly struct FileStat
    {
        public readonly long Size;
        public readonly DateTime CreationTimeUtc;
        public readonly DateTime CreationTime;
        public readonly DateTime LastWriteTimeUtc;
        public FileStat(long size, DateTime creationUtc, DateTime creation, DateTime writeUtc)
        {
            Size = size; CreationTimeUtc = creationUtc; CreationTime = creation; LastWriteTimeUtc = writeUtc;
        }
    }

    public static class FileExtensions
    {
        public static FileStat GetStat(this string path)
        {
            var fi = new FileInfo(path);
            DateTime creationUtc, creation;
            try { creationUtc = fi.CreationTimeUtc; creation = fi.CreationTime; }
            catch { creationUtc = fi.LastWriteTimeUtc; creation = fi.LastWriteTime; }
            return new FileStat(fi.Length, creationUtc, creation, fi.LastWriteTimeUtc);
        }
    }

    public static class Rules
    {
        public const string RegistryFileName = "processed.json";
        private const string WindowsIllegal = "<>:\"/\\|?*";

        private static DateTime CreationTime(string path)
        {
            var st = path.GetStat();
            return Environment.OSVersion.Platform == PlatformID.Win32NT ? st.CreationTime : st.LastWriteTimeUtc.ToLocalTime();
        }

        public static string ExtractDate(string path)
        {
            return CreationTime(path).ToString("yyyy-MM-dd");
        }

        public static string SanitizeName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name) sb.Append(WindowsIllegal.IndexOf(c) >= 0 ? '_' : c);
            var joined = sb.ToString();
            var cleaned = new StringBuilder();
            foreach (var c in joined) if (!char.IsControl(c)) cleaned.Append(c);
            var text = cleaned.ToString().Trim().TrimEnd('.');
            var parts = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            text = string.Join("_", parts).Trim('_');
            return text.Length > 0 ? text : "file";
        }

        public static string RegistryPath() { return Path.Combine(AppConfig.ConfigDir(), RegistryFileName); }

        public static string RegistryKey(string path)
        {
            var st = path.GetStat();
            var epoch = (long)(st.CreationTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds;
            return Path.GetFullPath(path) + "|" + st.Size + "|" + epoch;
        }

        public static Dictionary<string, string> LoadRegistry()
        {
            var p = RegistryPath();
            if (!File.Exists(p)) return new Dictionary<string, string>();
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8));
                if (node is JsonObject)
                {
                    var reg = new Dictionary<string, string>();
                    foreach (var kv in (JsonObject)node)
                        reg[kv.Key] = kv.Value == null ? "" : (string)kv.Value;
                    return reg;
                }
            }
            catch { }
            return new Dictionary<string, string>();
        }

        public static void SaveRegistry(Dictionary<string, string> reg)
        {
            var p = RegistryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var obj = new JsonObject();
            foreach (var kv in reg) obj[kv.Key] = kv.Value;
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString(ConfigJson.Options));
            File.Delete(p);
            File.Move(tmp, p);
        }

        public static bool IsProcessed(string path, out string outputName)
        {
            string name;
            var found = LoadRegistry().TryGetValue(RegistryKey(path), out name);
            outputName = found ? name : null;
            return found;
        }

        public static void MarkProcessed(string path, string outputName)
        {
            var reg = LoadRegistry();
            reg[RegistryKey(path)] = outputName;
            SaveRegistry(reg);
        }
    }
}
