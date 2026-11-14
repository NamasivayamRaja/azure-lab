using System.Text;
using System.Text.RegularExpressions;

namespace AzureLabs.Api.Storage
{
    public sealed record FileRule(string ContentType, int SniffBytes, Func<ReadOnlyMemory<byte>, bool, string?> Check);

    public static class DocumentFileRules
    {
        public const long MaxBytes = 10 * 1024 * 1024;

        private static readonly Regex SafeName =
            new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.Compiled);

        private static readonly UTF8Encoding StrictUtf8 =
            new(false, throwOnInvalidBytes: true);

        private static readonly Dictionary<string, FileRule> Allowed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [".csv"] = new("text/csv", 4096, CheckCsv)
            };

        public static bool IsSafeName(string n) =>
            SafeName.IsMatch(n) && !n.Contains("..");

        public static bool TryGetRule(string fileName, out FileRule rule)
        {
            rule = null!;
            return IsSafeName(fileName) &&
                   Allowed.TryGetValue(Path.GetExtension(fileName), out rule!);
        }

        // head = first N bytes; isWholeFile = true if the file is smaller than N
        private static string? CheckCsv(ReadOnlyMemory<byte> head, bool isWholeFile)
        {
            var span = head.Span;
            if (span.IndexOf((byte)0) >= 0)
                return "Binary content is not allowed in CSV.";

            // Don’t cut a multi-byte UTF-8 character in half
            if (!isWholeFile)
            {
                var lastNl = span.LastIndexOf((byte)'\n');
                if (lastNl < 0)
                    return "CSV header line is too long.";
                span = span[..(lastNl + 1)];
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(span); // rejects non-UTF-8 input
            }
            catch (DecoderFallbackException)
            {
                return "CSV must be UTF-8 encoded.";
            }

            var header = text.TrimStart('\uFEFF')
                             .Split('\n')[0]
                             .TrimEnd('\r');

            return header.Contains(',')
                ? null
                : "CSV header row must contain comma-separated columns.";
        }

        public static async Task<(FileRule? Rule, string FileName, string? Error)>
            ValidateAsync(IFormFile? file, CancellationToken ct)
        {
            if (file is null || file.Length == 0)
                return (null, "", "File is required.");

            if (file.Length > MaxBytes)
                return (null, "", "File exceeds 10 MB.");

            var fileName = Path.GetFileName(file.FileName);
            if (!TryGetRule(fileName, out var rule))
                return (null, "", "Only .csv files are allowed.");

            var buf = new byte[Math.Min(rule.SniffBytes, file.Length)];
            await using (var s = file.OpenReadStream())
                await s.ReadExactlyAsync(buf, ct);

            var error = rule.Check(buf, file.Length <= rule.SniffBytes);
            return error is null ? (rule, fileName, null) : (null, "", error);
        }
    }

}