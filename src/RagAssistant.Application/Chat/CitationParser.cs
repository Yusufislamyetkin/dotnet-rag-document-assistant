using System.Text.RegularExpressions;
using RagAssistant.Application.Retrieval;

namespace RagAssistant.Application.Chat;

public sealed record Citation(
    string Alias, Guid DocumentId, string FileName, int Page, Guid ChunkId, string Snippet);

public sealed record CitationParseResult(IReadOnlyList<Citation> Citations, IReadOnlyList<string> Invalid);

public static partial class CitationParser
{
    /// <summary>
    /// Extracts [docN:PAGE] markers (also "[doc1:3, doc2:5]" and "[doc1: p. 3]") and resolves them against the chunks
    /// that were actually in the prompt. A citation is valid only if the alias exists and the page lies inside one of
    /// that document's retrieved chunks; anything else is reported as invalid (hallucinated reference).
    /// </summary>
    public static CitationParseResult Parse(string answer, IReadOnlyList<SourceAlias> aliases, IReadOnlyList<RetrievedChunk> chunks)
    {
        var citations = new List<Citation>();
        var invalid = new List<string>();
        var seen = new HashSet<(string, int)>();

        foreach (Match bracket in Bracket().Matches(answer))
        {
            foreach (Match m in Reference().Matches(bracket.Groups[1].Value))
            {
                var alias = "doc" + m.Groups["n"].Value;
                var page = int.Parse(m.Groups["page"].Value);
                if (!seen.Add((alias, page))) continue;

                var source = aliases.FirstOrDefault(a => a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
                var chunk = source is null ? null : chunks
                    .Where(c => c.DocumentId == source.DocumentId && page >= c.PageStart && page <= c.PageEnd)
                    .OrderByDescending(c => c.FusedScore)
                    .FirstOrDefault();

                if (source is null || chunk is null)
                {
                    invalid.Add($"{alias}:{page}");
                    continue;
                }
                var snippet = chunk.Segments().FirstOrDefault(s => s.Page == page).Text;
                citations.Add(new Citation(source.Alias, source.DocumentId, source.FileName, page, chunk.ChunkId,
                    string.IsNullOrEmpty(snippet) ? chunk.Content : snippet));
            }
        }
        return new CitationParseResult(citations, invalid);
    }

    [GeneratedRegex(@"\[((?:\s*doc\d+\s*:\s*(?:p\.?\s*)?\d+\s*[,;]?)+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Bracket();

    [GeneratedRegex(@"doc(?<n>\d+)\s*:\s*(?:p\.?\s*)?(?<page>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Reference();
}
