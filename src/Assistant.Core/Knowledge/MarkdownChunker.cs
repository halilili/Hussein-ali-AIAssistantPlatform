using System.Text;

namespace Assistant.Core.Knowledge;

public sealed record Chunk(int Index, string Heading, string Text);

/// <summary>
/// Heading-aware chunker for tenant knowledge (FAQ, policies, menus).
/// 1. Split by markdown headings so a chunk never mixes two topics ("Insurance" vs "Parking").
/// 2. Split long sections by paragraph up to MaxChars, carrying a small overlap.
/// 3. Prefix each chunk with its heading path ("Fees and Insurance > Insurance") — a contextual
///    header that measurably improves retrieval for short chunks.
/// </summary>
public static class MarkdownChunker
{
    public static IReadOnlyList<Chunk> Split(string markdown, int maxChars = 900, int overlapChars = 120)
    {
        var chunks = new List<Chunk>();
        var headings = new string[6];
        var section = new StringBuilder();

        void FlushSection()
        {
            var body = section.ToString().Trim();
            section.Clear();
            if (body.Length == 0) return;
            var path = string.Join(" > ", headings.Where(h => !string.IsNullOrEmpty(h)));
            foreach (var piece in SplitLong(body, maxChars, overlapChars))
                chunks.Add(new Chunk(chunks.Count, path, $"{path}\n{piece}"));
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var level = line.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 6 && line.Length > level && line[level] == ' ')
            {
                FlushSection();
                headings[level - 1] = line[(level + 1)..].Trim();
                for (var i = level; i < headings.Length; i++) headings[i] = string.Empty;
                continue;
            }
            section.AppendLine(line);
        }
        FlushSection();
        return chunks;
    }

    private static IEnumerable<string> SplitLong(string body, int maxChars, int overlapChars)
    {
        if (body.Length <= maxChars) { yield return body; yield break; }

        var current = new StringBuilder();
        foreach (var paragraph in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > maxChars)
            {
                var text = current.ToString().Trim();
                yield return text;
                current.Clear();
                // overlap: carry the tail of the previous chunk so a sentence split across chunks stays findable
                if (text.Length > overlapChars) current.Append(text[^overlapChars..]).Append("\n\n");
            }
            current.Append(paragraph).Append("\n\n");
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }
}
