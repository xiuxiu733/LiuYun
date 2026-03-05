using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiuYun.Models;

namespace LiuYun.Services
{
    public enum ClipboardSemanticType
    {
        Image,
        Json,
        FilePath,
        Link,
        Email,
        LongNumber,
        Code,
        Text
    }

    public sealed class ClipboardSemanticInfo
    {
        public ClipboardSemanticType Type { get; init; } = ClipboardSemanticType.Text;
        public string Label { get; init; } = "Text";
        public string Glyph { get; init; } = "\uE8C8";
        public string PreviewTitle { get; init; } = "Text";
        public string PreviewSubtitle { get; init; } = string.Empty;
        public string PreviewBody { get; init; } = string.Empty;
        public bool HasOpenAction { get; init; }
        public string OpenActionGlyph { get; init; } = "\uE8A7";
        public string OpenTarget { get; init; } = string.Empty;
    }

    public static class ClipboardContentClassifierService
    {
        private static readonly Regex EmailRegex = new Regex(
            @"^[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DrivePathRegex = new Regex(
            @"^[A-Za-z]:\\",
            RegexOptions.Compiled);

        private static readonly Regex UncPathRegex = new Regex(
            @"^\\\\[^\\]+\\[^\\]+",
            RegexOptions.Compiled);

        private static readonly Regex LongNumberRegex = new Regex(
            @"^[\d\s-]{12,40}$",
            RegexOptions.Compiled);

        private static readonly Regex CodeKeywordRegex = new Regex(
            @"\b(class|public|private|protected|return|if|else|for|while|using|namespace|import|function|const|let|var|SELECT|INSERT|UPDATE|DELETE|FROM|WHERE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static ClipboardSemanticInfo Classify(ClipboardItem item)
        {
            if (item.ContentType == ClipboardContentType.Image)
            {
                string fileName = Path.GetFileName(item.ImagePath);
                return new ClipboardSemanticInfo
                {
                    Type = ClipboardSemanticType.Image,
                    Label = "Image",
                    Glyph = "\uE91B",
                    PreviewTitle = string.IsNullOrWhiteSpace(fileName) ? "Image" : fileName,
                    PreviewSubtitle = item.ImagePath,
                    PreviewBody = string.Empty,
                    HasOpenAction = !string.IsNullOrWhiteSpace(item.ImagePath),
                    OpenTarget = item.ImagePath
                };
            }

            string text = (item.TextContent ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                return BuildTextInfo("Text", "\uE8C8", string.Empty, string.Empty, "(Empty)");
            }

            if (LooksLikeJson(text))
            {
                return BuildTextInfo("JSON", "\uE943", string.Empty, string.Empty, BuildBody(text));
            }

            if (TryGetPathTarget(text, out string pathTarget, out string fileNameOrPath))
            {
                return new ClipboardSemanticInfo
                {
                    Type = ClipboardSemanticType.FilePath,
                    Label = "File",
                    Glyph = "\uE8B7",
                    PreviewTitle = fileNameOrPath,
                    PreviewSubtitle = pathTarget,
                    PreviewBody = string.Empty,
                    HasOpenAction = true,
                    OpenTarget = pathTarget
                };
            }

            if (TryGetLinkTarget(text, out string linkTarget))
            {
                string host = string.Empty;
                try
                {
                    host = new Uri(linkTarget).Host;
                }
                catch
                {
                    host = linkTarget;
                }

                return new ClipboardSemanticInfo
                {
                    Type = ClipboardSemanticType.Link,
                    Label = "Link",
                    Glyph = "\uE71B",
                    PreviewTitle = string.IsNullOrWhiteSpace(host) ? "Link" : host,
                    PreviewSubtitle = linkTarget,
                    PreviewBody = string.Empty,
                    HasOpenAction = true,
                    OpenTarget = linkTarget
                };
            }

            if (EmailRegex.IsMatch(text))
            {
                return new ClipboardSemanticInfo
                {
                    Type = ClipboardSemanticType.Email,
                    Label = "Email",
                    Glyph = "\uE715",
                    PreviewTitle = text,
                    PreviewSubtitle = string.Empty,
                    PreviewBody = string.Empty,
                    HasOpenAction = true,
                    OpenTarget = $"mailto:{text}"
                };
            }

            if (LooksLikeLongNumber(text))
            {
                return BuildTextInfo("Number", "\uE8C8", string.Empty, string.Empty, text);
            }

            if (LooksLikeCode(text))
            {
                return BuildTextInfo("Code", "\uE943", string.Empty, string.Empty, BuildBody(text));
            }

            return BuildTextInfo("Text", "\uE8C8", string.Empty, string.Empty, BuildBody(text));
        }

        private static ClipboardSemanticInfo BuildTextInfo(string label, string glyph, string title, string subtitle, string body)
        {
            return new ClipboardSemanticInfo
            {
                Type = label switch
                {
                    "JSON" => ClipboardSemanticType.Json,
                    "Code" => ClipboardSemanticType.Code,
                    "Number" => ClipboardSemanticType.LongNumber,
                    _ => ClipboardSemanticType.Text
                },
                Label = label,
                Glyph = glyph,
                PreviewTitle = title,
                PreviewSubtitle = subtitle,
                PreviewBody = body,
                HasOpenAction = false
            };
        }

        private static bool LooksLikeJson(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length < 2 || trimmed.Length > 200_000)
            {
                return false;
            }

            char first = trimmed[0];
            if (first != '{' && first != '[')
            {
                return false;
            }

            try
            {
                using var _ = JsonDocument.Parse(trimmed);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPathTarget(string text, out string pathTarget, out string fileNameOrPath)
        {
            pathTarget = string.Empty;
            fileNameOrPath = string.Empty;

            string firstLine = GetFirstNonEmptyLine(text);
            if (string.IsNullOrEmpty(firstLine))
            {
                return false;
            }

            firstLine = firstLine.Trim().Trim('"');

            if (Uri.TryCreate(firstLine, UriKind.Absolute, out Uri? fileUri) && fileUri.IsFile)
            {
                pathTarget = fileUri.LocalPath;
            }
            else if (DrivePathRegex.IsMatch(firstLine) || UncPathRegex.IsMatch(firstLine))
            {
                pathTarget = firstLine;
            }
            else
            {
                return false;
            }

            string candidate = pathTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(candidate);
            fileNameOrPath = string.IsNullOrWhiteSpace(name) ? candidate : name;
            return true;
        }

        private static bool TryGetLinkTarget(string text, out string linkTarget)
        {
            linkTarget = string.Empty;
            string firstLine = GetFirstNonEmptyLine(text);
            if (string.IsNullOrEmpty(firstLine))
            {
                return false;
            }

            firstLine = firstLine.Trim();
            if (!Uri.TryCreate(firstLine, UriKind.Absolute, out Uri? uri))
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            linkTarget = uri.AbsoluteUri;
            return true;
        }

        private static bool LooksLikeLongNumber(string text)
        {
            string trimmed = text.Trim();
            if (!LongNumberRegex.IsMatch(trimmed))
            {
                return false;
            }

            int digitCount = trimmed.Count(char.IsDigit);
            return digitCount >= 12;
        }

        private static bool LooksLikeCode(string text)
        {
            int score = 0;

            if (text.Contains("```", StringComparison.Ordinal))
            {
                score += 2;
            }

            if (text.Count(c => c == '\n') >= 1)
            {
                score += 1;
            }

            if ((text.Contains("{", StringComparison.Ordinal) && text.Contains("}", StringComparison.Ordinal)) ||
                (text.Contains("(", StringComparison.Ordinal) && text.Contains(")", StringComparison.Ordinal)))
            {
                score += 1;
            }

            if (text.Contains(";", StringComparison.Ordinal) || text.Contains("=>", StringComparison.Ordinal))
            {
                score += 1;
            }

            if (CodeKeywordRegex.IsMatch(text))
            {
                score += 1;
            }

            return score >= 3;
        }

        private static string BuildBody(string text)
        {
            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (normalized.Length <= 220)
            {
                return normalized;
            }

            return normalized.Substring(0, 220) + "...";
        }

        private static string GetFirstNonEmptyLine(string text)
        {
            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrEmpty(line)) ?? string.Empty;
        }
    }
}
