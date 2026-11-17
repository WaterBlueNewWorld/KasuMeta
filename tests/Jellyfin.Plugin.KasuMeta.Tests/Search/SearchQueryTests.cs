using System.Text;
using Jellyfin.Plugin.KasuMeta.Search;

namespace Jellyfin.Plugin.KasuMeta.Tests.Search;

public class SearchQueryTests
{
    [Fact]
    public void Words_go_to_the_index_with_the_last_as_a_prefix()
    {
        var query = SearchQuery.Parse("  La cabaña del terr ");

        Assert.Equal("La cabaña del terr", query.Text);
        Assert.Equal(["La", "cabaña", "del", "terr"], query.WordTerms);
        Assert.Empty(query.SubstringTerms);
        Assert.Equal("\"La\" \"cabaña\" \"del\" \"terr\" *", query.ToMatchExpression());
    }

    [Theory]
    [InlineData("千尋", "千尋")]
    [InlineData("レヴュー・スタァライト", "レヴュー|スタァライト")] // "・" separates; "ー" is a letter
    [InlineData("少女☆歌劇", "少女|歌劇")]
    [InlineData("마법소녀", "마법소녀")]
    [InlineData("สวัสดี", "สวัสดี")]
    public void Scripts_without_spaces_become_substring_terms(string text, string expected)
    {
        var query = SearchQuery.Parse(text);

        Assert.Equal(expected.Split('|'), query.SubstringTerms);
        Assert.Empty(query.WordTerms);
        Assert.Null(query.ToMatchExpression());
    }

    [Fact]
    public void Combining_marks_stay_inside_their_word()
    {
        // "cabaña" typed with a separate tilde, and a Devanagari word with a virama and vowel signs.
        Assert.Equal(["caban\u0303a"], SearchQuery.Parse("caban\u0303a").WordTerms);
        Assert.Equal(["हिन्दी", "मीडियम"], SearchQuery.Parse("हिन्दी मीडियम").WordTerms);
        // A mark with no letter before it starts nothing.
        Assert.Equal(["a"], SearchQuery.Parse("\u0301 a").WordTerms);
    }

    [Fact]
    public void A_mixed_query_splits_by_script()
    {
        var query = SearchQuery.Parse("Revue 少女 Starlight2");

        Assert.Equal(["Revue", "Starlight2"], query.WordTerms);
        Assert.Equal(["少女"], query.SubstringTerms);
    }

    [Fact]
    public void A_term_mixing_scripts_without_a_separator_is_matched_as_a_substring()
    {
        Assert.Equal(["abc漢字"], SearchQuery.Parse("abc漢字").SubstringTerms);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! --- \"\" %_")]
    public void Nothing_searchable_is_empty(string? text)
    {
        Assert.True(SearchQuery.Parse(text).IsEmpty);
    }

    [Fact]
    public void Fts_operators_and_quotes_are_plain_words()
    {
        var query = SearchQuery.Parse("rock AND \"roll\" OR NEAR(x) na*me col:on");

        // Everything is quoted, and punctuation never reaches the expression.
        Assert.Equal(
            "\"rock\" \"AND\" \"roll\" \"OR\" \"NEAR\" \"x\" \"na\" \"me\" \"col\" \"on\" *",
            query.ToMatchExpression());
    }

    [Fact]
    public void Long_input_is_cut_and_terms_are_capped()
    {
        var query = SearchQuery.Parse(string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i}")));

        Assert.True(query.Text.Length <= SearchQuery.MaxLength);
        Assert.Equal(SearchQuery.MaxTerms, query.WordTerms.Count);
    }

    [Theory]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%a\_b%")]
    [InlineData(@"c:\x", @"%c:\\x%")]
    [InlineData("千尋", "%千尋%")]
    public void Like_patterns_escape_wildcards(string term, string expected)
    {
        Assert.Equal(expected, SearchQuery.ToLikePattern(term));
    }

    [Theory]
    [InlineData("La Cabaña", "la cabana")]
    [InlineData("CONSPIRACIÓN", "conspiracion")]
    [InlineData("Ångström Über", "angstrom uber")]
    [InlineData("千と千尋", "千と千尋")]
    public void Fold_lowers_case_and_drops_accents(string text, string expected)
    {
        Assert.Equal(expected, SearchQuery.Fold(text));
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("ñ", false)]
    [InlineData("я", false)]
    [InlineData("千", true)]
    [InlineData("ひ", true)]
    [InlineData("カ", true)]
    [InlineData("한", true)]
    [InlineData("ก", true)]
    public void Unsegmented_scripts_are_recognized(string character, bool expected)
    {
        Assert.Equal(expected, SearchQuery.IsUnsegmentedScript(Rune.GetRuneAt(character, 0)));
    }
}
