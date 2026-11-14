using System.Text;
using System.Text.RegularExpressions;

namespace AzureLab.Rag.Ingestion;

public static partial class TextChunker
{
    public static IReadOnlyList<string> Chunk(string text, int size, int overlap)
    {
        var maxUnit = size - overlap - 1;          // guarantees tail + ' ' + unit <= size
        var chunks = new List<string>();
        var current = new StringBuilder();
        var tail = "";

        foreach (var unit in SplitIntoUnits(text, maxUnit))
        {
            if (current.Length > 0 && current.Length + 1 + unit.Length > size)
            {
                var finished = current.ToString();
                chunks.Add(finished);
                tail = OverlapTail(finished, overlap);   // only used if another unit follows
                current.Clear();
            }

            if (current.Length == 0 && tail.Length > 0) { current.Append(tail); tail = ""; }
            if (current.Length > 0) current.Append(' ');
            current.Append(unit);
        }

        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static IEnumerable<string> SplitIntoUnits(string text, int max)
    {
        foreach (var paragraph in ParagraphBreak().Split(text.Replace("\r\n", "\n")))
        {
            var p = Whitespace().Replace(paragraph, " ").Trim();
            if (p.Length == 0) continue;

            if (p.Length <= max) { yield return p; continue; }

            foreach (var sentence in SentenceBreak().Split(p))
                foreach (var piece in HardSplit(sentence.Trim(), max))
                    yield return piece;
        }
    }

    private static IEnumerable<string> HardSplit(string s, int max)
    {
        while (s.Length > max)
        {
            var cut = s.LastIndexOf(' ', max);
            if (cut <= 0) cut = max;
            yield return s[..cut];
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }

    private static string OverlapTail(string finished, int overlap)
    {
        if (overlap <= 0 || finished.Length <= overlap) return "";
        var tail = finished[^overlap..];
        var firstSpace = tail.IndexOf(' ');
        return firstSpace >= 0 && firstSpace < tail.Length - 1 ? tail[(firstSpace + 1)..] : tail;
    }

    [GeneratedRegex(@"\n\s*\n")] private static partial Regex ParagraphBreak();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"(?<=[.!?])\s+")] private static partial Regex SentenceBreak();
}