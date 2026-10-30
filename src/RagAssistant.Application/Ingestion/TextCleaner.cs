using System.Text;
using System.Text.RegularExpressions;

namespace RagAssistant.Application.Ingestion;

/// <summary>Normalizes text extracted from PDFs. Character offsets stored on chunks refer to the cleaned text.</summary>
public static partial class TextCleaner
{
    private static readonly string SoftHyphen = ((char)0xAD).ToString();

    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Normalize(NormalizationForm.FormKC);
        text = text.Replace(SoftHyphen, ""); // invisible break hints, not real hyphens
        text = ControlChars().Replace(text, " ");
        // Re-join words hyphenated across line breaks: "manage-\nment" -> "management".
        text = HyphenatedLineBreak().Replace(text, "$1$2");
        // Paragraph breaks survive as "\n\n", single line breaks become spaces.
        text = ParagraphBreak().Replace(text, "\u0000");
        text = text.Replace('\n', ' ');
        text = MultiSpace().Replace(text, " ");
        text = text.Replace(" \u0000 ", "\n\n").Replace("\u0000 ", "\n\n").Replace(" \u0000", "\n\n").Replace("\u0000", "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"(\p{L})-\n[ \t]*(\p{Ll})")]
    private static partial Regex HyphenatedLineBreak();

    [GeneratedRegex(@"[ \t]*\n[ \t]*\n\s*")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"[ \t ]{2,}|\t")]
    private static partial Regex MultiSpace();
}
