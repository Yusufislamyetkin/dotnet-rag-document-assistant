using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Ingestion;
using RagAssistant.Infrastructure.Ingestion;

namespace RagAssistant.UnitTests;

/// <summary>Counts whitespace-separated words, so expected chunk sizes are easy to reason about.</summary>
internal sealed class WordTokenCounter : ITokenCounter
{
    public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

public class TextChunkerTests
{
    private readonly TextChunker _chunker = new(new WordTokenCounter());

    private static string Sentence(int i, int words = 10) =>
        "Sentence" + i + " " + string.Join(' ', Enumerable.Range(1, words - 2).Select(w => "w" + w)) + " end.";

    private static string Paragraph(int from, int count, int words = 10) =>
        string.Join(' ', Enumerable.Range(from, count).Select(i => Sentence(i, words)));

    [Fact]
    public void Short_text_becomes_a_single_chunk_with_exact_offsets()
    {
        var text = "First sentence here. Second sentence here.";
        var chunks = _chunker.Chunk([new PageText(1, text)], maxTokens: 100, overlapTokens: 10);

        var chunk = Assert.Single(chunks);
        Assert.Equal(text, chunk.Content);
        Assert.Equal((1, 0, 1, text.Length), (chunk.PageStart, chunk.CharStart, chunk.PageEnd, chunk.CharEnd));
    }

    [Fact]
    public void Chunks_respect_the_token_limit_and_end_on_sentence_boundaries()
    {
        var chunks = _chunker.Chunk([new PageText(1, Paragraph(1, 30))], maxTokens: 45, overlapTokens: 10);

        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c =>
        {
            Assert.True(c.TokenCount <= 45, $"chunk has {c.TokenCount} tokens");
            Assert.EndsWith("end.", c.Content);
            Assert.StartsWith("Sentence", c.Content);
        });
    }

    [Fact]
    public void Consecutive_chunks_overlap_by_whole_trailing_sentences()
    {
        var chunks = _chunker.Chunk([new PageText(1, Paragraph(1, 12))], maxTokens: 40, overlapTokens: 10);

        for (var i = 1; i < chunks.Count; i++)
        {
            var lastSentenceOfPrevious = chunks[i - 1].Content[(chunks[i - 1].Content.LastIndexOf("Sentence", StringComparison.Ordinal))..];
            Assert.StartsWith(lastSentenceOfPrevious, chunks[i].Content);
        }
    }

    [Fact]
    public void Zero_overlap_produces_disjoint_chunks_covering_every_sentence_once()
    {
        var chunks = _chunker.Chunk([new PageText(1, Paragraph(1, 12))], maxTokens: 40, overlapTokens: 0);

        var sentences = chunks.SelectMany(c => c.Content.Split("end.", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        Assert.Equal(12, sentences.Count);
        Assert.Equal(12, sentences.Distinct().Count());
    }

    [Fact]
    public void Page_numbers_and_character_offsets_point_back_into_the_page_text()
    {
        var pages = new[] { new PageText(1, Paragraph(1, 5)), new PageText(2, Paragraph(6, 5)), new PageText(3, Paragraph(11, 5)) };
        var chunks = _chunker.Chunk(pages, maxTokens: 35, overlapTokens: 0);

        Assert.Contains(chunks, c => c.PageStart != c.PageEnd); // at least one chunk spans a page break
        foreach (var c in chunks)
        {
            var startPage = pages.Single(p => p.PageNumber == c.PageStart).Text;
            var endPage = pages.Single(p => p.PageNumber == c.PageEnd).Text;
            Assert.Equal(c.Content[..20], startPage.Substring(c.CharStart, 20));
            Assert.Equal(c.Content[^10..], endPage[(c.CharEnd - 10)..c.CharEnd]);
        }
        Assert.Equal(1, chunks[0].PageStart);
        Assert.Equal(3, chunks[^1].PageEnd);
    }

    [Fact]
    public void Page_map_splits_chunk_content_back_into_per_page_text()
    {
        var pages = new[] { new PageText(4, "Alpha one. Alpha two."), new PageText(5, "Beta one. Beta two.") };
        var chunk = Assert.Single(_chunker.Chunk(pages, maxTokens: 100, overlapTokens: 0));

        Assert.Equal([4, 5], chunk.PageNumbers);
        var segments = RagAssistant.Domain.DocumentChunk.Segments(chunk.Content, chunk.PageStart, chunk.PageNumbers, chunk.PageOffsets).ToList();
        Assert.Equal([(4, "Alpha one. Alpha two."), (5, "Beta one. Beta two.")], segments);
    }

    [Fact]
    public void Oversized_sentence_is_split_on_word_boundaries()
    {
        var longSentence = string.Join(' ', Enumerable.Range(1, 120).Select(i => "word" + i)) + ".";
        var chunks = _chunker.Chunk([new PageText(1, longSentence)], maxTokens: 50, overlapTokens: 0);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 50));
        Assert.Equal(longSentence, string.Join(' ', chunks.Select(c => c.Content)));
    }

    [Fact]
    public void Abbreviation_like_lowercase_continuations_do_not_split_sentences()
    {
        var text = "Use the e.g. clause carefully. Then continue.";
        var sentences = _chunker.SplitSentences(new PageText(1, text), 100).Select(s => s.Text).ToList();

        Assert.Equal(["Use the e.g. clause carefully.", "Then continue."], sentences);
    }

    [Fact]
    public void Overlap_must_be_smaller_than_chunk_size()
    {
        Assert.Throws<ArgumentException>(() => _chunker.Chunk([new PageText(1, "A b.")], 50, 50));
    }

    [Fact]
    public void Works_with_the_real_tokenizer()
    {
        var chunker = new TextChunker(new TiktokenTokenCounter());
        var chunks = chunker.Chunk([new PageText(1, Paragraph(1, 200, 20))], maxTokens: 800, overlapTokens: 100);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 800));
    }
}

public class TextCleanerTests
{
    [Fact]
    public void Joins_words_hyphenated_across_lines_and_keeps_paragraphs()
    {
        var raw = "Risk manage-\nment is con-\ntinuous.\nNext line\n\n\nNew  paragraph\twith­tabs.";
        Assert.Equal("Risk management is continuous. Next line\n\nNew paragraph withtabs.", TextCleaner.Clean(raw));
    }

    [Fact]
    public void Keeps_real_hyphens_before_uppercase_words()
    {
        Assert.Equal("Socio- Technical systems", TextCleaner.Clean("Socio-\nTechnical systems"));
    }

    [Fact]
    public void Empty_and_whitespace_input_returns_empty()
    {
        Assert.Equal("", TextCleaner.Clean("  \n\t "));
    }
}
