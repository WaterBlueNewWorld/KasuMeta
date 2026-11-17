using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.KasuMeta.Search;

/// <summary>
/// A user's search text, split into the two kinds of term the store can match.
/// </summary>
/// <remarks>
/// SQLite's unicode61 tokenizer splits on spaces and punctuation, so a title in a script written
/// without spaces is one long token: full-text search finds 千と千尋の神隠し by its start but
/// never by 千尋 in the middle (checked against SQLite). Terms in such scripts are therefore
/// matched as substrings instead; every other term goes to the full-text index, where case and
/// accents are folded and the last term matches as a prefix (type-ahead).
/// </remarks>
public sealed class SearchQuery
{
    /// <summary>Longest query accepted, in characters.</summary>
    public const int MaxLength = 200;

    /// <summary>Most terms used; the rest are ignored.</summary>
    public const int MaxTerms = 12;

    private SearchQuery(string text, IReadOnlyList<string> wordTerms, IReadOnlyList<string> substringTerms)
    {
        Text = text;
        WordTerms = wordTerms;
        SubstringTerms = substringTerms;
    }

    /// <summary>Gets the query as typed, trimmed.</summary>
    public string Text { get; }

    /// <summary>Gets the terms for the full-text index, in order (the last one is a prefix).</summary>
    public IReadOnlyList<string> WordTerms { get; }

    /// <summary>Gets the terms matched as substrings (scripts without spaces).</summary>
    public IReadOnlyList<string> SubstringTerms { get; }

    /// <summary>Gets a value indicating whether there is nothing to search for.</summary>
    public bool IsEmpty => WordTerms.Count == 0 && SubstringTerms.Count == 0;

    /// <summary>Splits the text on anything that isn't a letter or digit and sorts the terms.</summary>
    public static SearchQuery Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length > MaxLength)
        {
            trimmed = trimmed[..MaxLength];
        }

        var words = new List<string>();
        var substrings = new List<string>();
        var term = new StringBuilder();
        var unsegmented = false;

        void Flush()
        {
            if (term.Length > 0 && words.Count + substrings.Count < MaxTerms)
            {
                (unsegmented ? substrings : words).Add(term.ToString());
            }

            term.Clear();
            unsegmented = false;
        }

        foreach (var rune in trimmed.EnumerateRunes())
        {
            // A combining mark belongs to the letter before it: Thai and Indic vowel signs,
            // Arabic and Hebrew points, an accent typed as a separate character. Splitting on it
            // would cut words in half (สวัสดี is one word with two such marks).
            if (Rune.IsLetterOrDigit(rune) || (term.Length > 0 && IsCombiningMark(rune)))
            {
                term.Append(rune.ToString());
                unsegmented |= IsUnsegmentedScript(rune);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return new SearchQuery(trimmed, words, substrings);
    }

    /// <summary>
    /// Builds the FTS5 MATCH expression: every term quoted (so AND, OR, NEAR and the like are
    /// plain words), all required, the last a prefix. Null when there are no word terms.
    /// SQLite tokenizes each quoted string with the index's own tokenizer, so a term it would
    /// split further (at a combining mark, say) becomes a phrase and still matches.
    /// </summary>
    public string? ToMatchExpression()
    {
        if (WordTerms.Count == 0)
        {
            return null;
        }

        var expression = new StringBuilder();
        for (var i = 0; i < WordTerms.Count; i++)
        {
            // Terms hold only letters and digits; doubling quotes is belt and braces.
            expression.Append('"').Append(WordTerms[i].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            expression.Append(i == WordTerms.Count - 1 ? " *" : " ");
        }

        return expression.ToString();
    }

    /// <summary>Builds a <c>LIKE … ESCAPE '\'</c> pattern that finds the term anywhere.</summary>
    public static string ToLikePattern(string term)
    {
        var escaped = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return "%" + escaped + "%";
    }

    /// <summary>
    /// Folds text for comparing names in code the way the index does: lower case, no accents.
    /// </summary>
    public static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }

        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// Whether a character belongs to a script normally written without spaces between words:
    /// Chinese, Japanese, Korean, Thai, Lao, Khmer, Myanmar.
    /// </summary>
    internal static bool IsUnsegmentedScript(Rune rune)
    {
        var v = rune.Value;
        return v is (>= 0x0E00 and <= 0x0EFF) // Thai, Lao
            or (>= 0x1000 and <= 0x109F) // Myanmar
            or (>= 0x1100 and <= 0x11FF) // Hangul Jamo
            or (>= 0x1780 and <= 0x17FF) // Khmer
            or (>= 0x3040 and <= 0x30FF) // Hiragana, Katakana
            or (>= 0x3100 and <= 0x318F) // Bopomofo, Hangul compatibility Jamo
            or (>= 0x31F0 and <= 0x31FF) // Katakana phonetic extensions
            or (>= 0x3400 and <= 0x4DBF) // CJK extension A
            or (>= 0x4E00 and <= 0x9FFF) // CJK unified ideographs
            or (>= 0xAC00 and <= 0xD7AF) // Hangul syllables
            or (>= 0xF900 and <= 0xFAFF) // CJK compatibility ideographs
            or (>= 0xFF66 and <= 0xFF9F) // Half-width Katakana
            or (>= 0x20000 and <= 0x3FFFF); // CJK extensions B and beyond
    }
}
