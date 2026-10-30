using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Ingestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RagAssistant.Infrastructure.Ingestion;

/// <summary>
/// Layout-aware extraction: words are built from glyph positions (so justified text keeps its spaces), grouped into
/// text blocks (Docstrum) and put in reading order, which keeps multi-column pages readable.
/// </summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public IReadOnlyList<PageText> Extract(Stream pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var pages = new List<PageText>(document.NumberOfPages);
            foreach (var page in document.GetPages())
            {
                var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
                if (words.Count == 0)
                {
                    pages.Add(new PageText(page.Number, ""));
                    continue;
                }
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
                pages.Add(new PageText(page.Number, string.Join("\n\n", ordered.Select(b => b.Text))));
            }
            return pages;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IngestionException)
        {
            throw new IngestionException($"Could not read PDF: {ex.Message}");
        }
    }
}
