using System.Text;
using System.Text.RegularExpressions;
using RagAssistant.Application.Abstractions;

namespace RagAssistant.Application.Ingestion;

/// <param name="PageNumbers">Pages the chunk touches, in order.</param>
/// <param name="PageOffsets">Offset in <paramref name="Content"/> where the text of the matching page starts.</param>
public sealed record ChunkDraft(int PageStart, int CharStart, int PageEnd, int CharEnd, string Content, int TokenCount,
    IReadOnlyList<int> PageNumbers, IReadOnlyList<int> PageOffsets);

/// <summary>
/// Sentence-aware chunker. Packs whole sentences into chunks of at most <c>maxTokens</c> and repeats the trailing
/// sentences of each chunk (up to <c>overlapTokens</c>) at the start of the next one. A chunk only breaks inside a
/// sentence when that single sentence is longer than <c>maxTokens</c>; it is then split on word boundaries.
/// </summary>
public sealed partial class TextChunker(ITokenCounter tokens)
{
    internal sealed record Sentence(int Page, int Start, int End, string Text, int Tokens);

    public IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<PageText> cleanedPages, int maxTokens, int overlapTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapTokens);
        if (overlapTokens >= maxTokens) throw new ArgumentException("Overlap must be smaller than the chunk size.");

        var sentences = cleanedPages.SelectMany(p => SplitSentences(p, maxTokens)).ToList();
        var chunks = new List<ChunkDraft>();
        var current = new List<Sentence>();
        var currentTokens = 0;

        foreach (var sentence in sentences)
        {
            if (current.Count > 0 && currentTokens + sentence.Tokens > maxTokens)
            {
                chunks.Add(Build(current));
                current = TakeOverlap(current, overlapTokens, maxTokens - sentence.Tokens);
                currentTokens = current.Sum(s => s.Tokens);
            }
            current.Add(sentence);
            currentTokens += sentence.Tokens;
        }
        if (current.Count > 0) chunks.Add(Build(current));
        return chunks;
    }

    private static List<Sentence> TakeOverlap(List<Sentence> previous, int overlapTokens, int budget)
    {
        var overlap = new List<Sentence>();
        var total = 0;
        // Never carry the whole previous chunk over, otherwise the chunker would not make progress.
        for (var i = previous.Count - 1; i >= 1; i--)
        {
            var s = previous[i];
            if (total + s.Tokens > overlapTokens || total + s.Tokens > budget) break;
            overlap.Insert(0, s);
            total += s.Tokens;
        }
        return overlap;
    }

    private ChunkDraft Build(List<Sentence> sentences)
    {
        var sb = new StringBuilder();
        var pages = new List<int>();
        var offsets = new List<int>();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            if (i == 0 || sentences[i].Page != sentences[i - 1].Page)
            {
                pages.Add(sentences[i].Page);
                offsets.Add(sb.Length);
            }
            sb.Append(sentences[i].Text);
        }
        var content = sb.ToString();
        return new ChunkDraft(sentences[0].Page, sentences[0].Start, sentences[^1].Page, sentences[^1].End,
            content, tokens.Count(content), pages, offsets);
    }

    internal IEnumerable<Sentence> SplitSentences(PageText page, int maxTokens)
    {
        var text = page.Text;
        var start = 0;
        foreach (Match m in SentenceBoundary().Matches(text))
        {
            var end = m.Index;
            foreach (var s in Emit(page.PageNumber, text, start, end, maxTokens)) yield return s;
            start = m.Index + m.Length;
        }
        foreach (var s in Emit(page.PageNumber, text, start, text.Length, maxTokens)) yield return s;
    }

    private IEnumerable<Sentence> Emit(int page, string text, int start, int end, int maxTokens)
    {
        // Trim whitespace while keeping offsets exact.
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (end <= start) yield break;

        var sentence = text[start..end];
        var count = tokens.Count(sentence);
        if (count <= maxTokens)
        {
            yield return new Sentence(page, start, end, sentence, count);
            yield break;
        }

        // Oversized sentence (tables, lists without punctuation): fall back to word boundaries.
        var pieceStart = start;
        var lastSpace = -1;
        for (var i = start; i <= end; i++)
        {
            if (i < end && !char.IsWhiteSpace(text[i])) continue;
            var candidate = text[pieceStart..i];
            if (tokens.Count(candidate) > maxTokens && lastSpace > pieceStart)
            {
                var piece = text[pieceStart..lastSpace];
                yield return new Sentence(page, pieceStart, lastSpace, piece, tokens.Count(piece));
                pieceStart = lastSpace + 1;
            }
            lastSpace = i;
        }
        if (pieceStart < end)
        {
            var piece = text[pieceStart..end];
            yield return new Sentence(page, pieceStart, end, piece, tokens.Count(piece));
        }
    }

    // End of sentence: terminal punctuation (optionally followed by closing quotes/brackets) and whitespace before an
    // uppercase letter, digit, quote or bracket. Paragraph breaks are always boundaries.
    [GeneratedRegex(@"(?<=[.!?][""'”’)\]]*)\s+(?=[\p{Lu}\d""'“‘(\[•])|\n\n")]
    private static partial Regex SentenceBoundary();
}
