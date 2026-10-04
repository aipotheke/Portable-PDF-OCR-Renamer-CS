using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfOcrRenamer
{
    public static class PdfOps
    {
        public const string AttachmentName = "ocr.md";
        public const string ProcessedSubdir = "processed";
        public const string MdSubdir = "md";
        public const int MaxPathLen = 250;

        public static string BuildTargetName(string source, string filetype, string sender)
        {
            var date = Rules.ExtractDate(source);
            var ft = Rules.SanitizeName(filetype);
            var stem = Rules.SanitizeName(Path.GetFileNameWithoutExtension(source));
            var parts = new List<string> { date, ft };
            var company = Rules.SanitizeName(sender ?? "").Trim(' ', '-', '_');
            var companyLower = company.ToLowerInvariant();
            if (company.Length > 0 && companyLower != "unknown" && companyLower != "file")
                parts.Add(company);
            parts.Add(stem);
            return string.Join("_", parts.ToArray()) + ".pdf";
        }

        private static string UniquePath(string directory, string filename)
        {
            var candidate = Path.Combine(directory, filename);
            if (!File.Exists(candidate)) return candidate;
            var stem = Path.GetFileNameWithoutExtension(filename);
            var suffix = Path.GetExtension(filename);
            for (var i = 1; ; i++)
            {
                candidate = Path.Combine(directory, stem + "_" + i + suffix);
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private static string TruncatePath(string directory, string filename)
        {
            var candidate = Path.Combine(directory, filename);
            if (candidate.Length <= MaxPathLen) return candidate;
            var stem = Path.GetFileNameWithoutExtension(filename);
            var suffix = Path.GetExtension(filename);
            var over = candidate.Length - MaxPathLen;
            if (over >= stem.Length) stem = "file";
            else stem = stem.Substring(0, stem.Length - over);
            return Path.Combine(directory, stem + suffix);
        }

        public static string EmbedAndWrite(string source, string markdown, string filetype, string watchFolder,
            bool keepMdSidecar, string sender)
        {
            var processedDir = Path.Combine(watchFolder, ProcessedSubdir);
            Directory.CreateDirectory(processedDir);
            var targetName = Rules.SanitizeName(BuildTargetName(source, filetype, sender));
            var target = UniquePath(processedDir, targetName);
            target = TruncatePath(Path.GetDirectoryName(target), Path.GetFileName(target));
            target = UniquePath(Path.GetDirectoryName(target), Path.GetFileName(target));

            var tmp = target + ".tmp";
            PdfAttach.EmbedAttachment(source, AttachmentName, Encoding.UTF8.GetBytes(markdown), tmp);
            File.Delete(target);
            File.Move(tmp, target);

            if (keepMdSidecar)
            {
                var mdDir = Path.Combine(watchFolder, MdSubdir);
                Directory.CreateDirectory(mdDir);
                var mdName = Path.GetFileNameWithoutExtension(target) + ".md";
                var mdTarget = UniquePath(mdDir, mdName);
                var mdTmp = mdTarget + ".tmp";
                File.WriteAllText(mdTmp, markdown, new UTF8Encoding(false));
                File.Delete(mdTarget);
                File.Move(mdTmp, mdTarget);
            }
            return target;
        }
    }
}
