using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfOcrRenamer;

public static class Rules
{
    public const string RegistryFileName = "processed.json";
    private const string WindowsIllegal = "<>:\"/\\|?*";

    private static DateTime CreationTime(string path)
    {
        var st = path.GetStat();
        return OperatingSystem.IsWindows() ? st.CreationTime : st.LastWriteTimeUtc;
    }

    public static string ExtractDate(string path) => CreationTime(path).ToLocalTime().ToString("yyyy-MM-dd");

    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(WindowsIllegal.Contains(c) ? '_' : c);
        var joined = sb.ToString();
        var cleaned = new string(joined.Where(c => !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        cleaned = string.Join("_", cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        cleaned = cleaned.Trim('_');
        return cleaned.Length > 0 ? cleaned : "file";
    }

    public static string RegistryPath() => Path.Combine(AppConfig.ConfigDir(), RegistryFileName);

    public static string RegistryKey(string path)
    {
        var st = path.GetStat();
        return $"{Path.GetFullPath(path)}|{st.Size}|{(long)(st.CreationTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds}";
    }

    public static Dictionary<string, string> LoadRegistry()
    {
        var p = RegistryPath();
        if (!File.Exists(p)) return new();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(p, Encoding.UTF8));
            if (node is JsonObject obj)
            {
                var reg = new Dictionary<string, string>();
                foreach (var kv in obj) reg[kv.Key] = kv.Value?.GetValue<string>() ?? "";
                return reg;
            }
        }
        catch { }
        return new();
    }

    public static void SaveRegistry(Dictionary<string, string> reg)
    {
        var p = RegistryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var obj = new JsonObject();
        foreach (var kv in reg) obj[kv.Key] = kv.Value;
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(ConfigJson.Options));
        File.Move(tmp, p, overwrite: true);
    }

    public static (bool Processed, string? OutputName) IsProcessed(string path)
    {
        var key = RegistryKey(path);
        return LoadRegistry().TryGetValue(key, out var name) ? (true, name) : (false, null);
    }

    public static void MarkProcessed(string path, string outputName)
    {
        var reg = LoadRegistry();
        reg[RegistryKey(path)] = outputName;
        SaveRegistry(reg);
    }
}

public readonly record struct FileStat(long Size, DateTime CreationTimeUtc, DateTime CreationTime, DateTime LastWriteTimeUtc);

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
