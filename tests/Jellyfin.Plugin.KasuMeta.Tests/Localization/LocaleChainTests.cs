using Jellyfin.Plugin.KasuMeta.Localization;

namespace Jellyfin.Plugin.KasuMeta.Tests.Localization;

public class LocaleChainTests
{
    [Theory]
    [InlineData("es-MX", "es-MX")]
    [InlineData("es_MX", "es-MX")]
    [InlineData("ES_mx", "es-MX")]
    [InlineData(" ja ", "ja")]
    [InlineData("zh-hant-tw", "zh-Hant-TW")]
    [InlineData("zh-HANS", "zh-Hans")]
    [InlineData("es-419", "es-419")]
    [InlineData("yue", "yue")]
    [InlineData("ja-Latn", "ja-Latn")]
    public void Normalizes_valid_codes(string input, string expected)
    {
        Assert.True(LocaleChain.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("e")]
    [InlineData("spanish")]
    [InlineData("es-MEX")]
    [InlineData("es-MX-x")]
    [InlineData("es--MX")]
    [InlineData("12-MX")]
    [InlineData("es-MX; DROP TABLE")]
    public void Rejects_invalid_codes(string? input)
    {
        Assert.False(LocaleChain.TryNormalize(input, out _));
        if (input is not null)
        {
            Assert.Throws<ArgumentException>(() => LocaleChain.Normalize(input));
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("es-MX", "es")]
    [InlineData("ja-Latn", "ja")]
    public void Fallback_groups_split_chinese_by_script(string locale, string group)
    {
        Assert.Equal(group, LocaleChain.FallbackGroup(locale));
    }

    [Fact]
    public void Exact_match_wins()
    {
        Assert.Equal("es-MX", LocaleChain.Resolve("es_mx", ["es", "es-ES", "es-MX"], fallbackToBase: true));
    }

    [Fact]
    public void Falls_back_to_the_bare_language_first()
    {
        Assert.Equal("es", LocaleChain.Resolve("es-MX", ["es-ES", "es", "en-US"], fallbackToBase: true));
    }

    [Fact]
    public void Then_to_the_preferred_sibling()
    {
        Assert.Equal(
            "es-ES",
            LocaleChain.Resolve("es-MX", ["es-AR", "es-ES"], fallbackToBase: true, siblingPreference: ["en-US", "es-ES", "es-AR"]));
    }

    [Fact]
    public void Then_to_any_sibling_alphabetically()
    {
        Assert.Equal("es-AR", LocaleChain.Resolve("es-MX", ["es-ES", "es-AR"], fallbackToBase: true));
    }

    [Fact]
    public void A_bare_request_finds_a_regional_value()
    {
        Assert.Equal("es-ES", LocaleChain.Resolve("es", ["es-ES", "en-US"], fallbackToBase: true));
    }

    [Fact]
    public void No_fallback_when_the_setting_is_off()
    {
        Assert.Null(LocaleChain.Resolve("es-MX", ["es", "es-ES"], fallbackToBase: false));
        Assert.Equal("es-MX", LocaleChain.Resolve("es-MX", ["es-MX"], fallbackToBase: false));
    }

    [Fact]
    public void Never_crosses_languages()
    {
        Assert.Null(LocaleChain.Resolve("es-MX", ["en-US", "pt-BR"], fallbackToBase: true));
    }

    [Fact]
    public void Traditional_and_simplified_chinese_never_stand_in_for_each_other()
    {
        Assert.Null(LocaleChain.Resolve("zh-TW", ["zh-CN"], fallbackToBase: true));
        Assert.Null(LocaleChain.Resolve("zh-CN", ["zh-TW", "zh-HK"], fallbackToBase: true));
        Assert.Equal("zh-HK", LocaleChain.Resolve("zh-TW", ["zh-CN", "zh-HK"], fallbackToBase: true));
        // Bare "zh" is Simplified, so it may serve zh-CN but not zh-TW.
        Assert.Equal("zh", LocaleChain.Resolve("zh-CN", ["zh"], fallbackToBase: true));
        Assert.Null(LocaleChain.Resolve("zh-TW", ["zh"], fallbackToBase: true));
    }

    [Fact]
    public void An_invalid_request_or_stored_locale_is_ignored()
    {
        Assert.Null(LocaleChain.Resolve("not a locale", ["es-MX"], fallbackToBase: true));
        Assert.Equal("es-MX", LocaleChain.Resolve("es-MX", ["garbage!", "es-MX"], fallbackToBase: true));
    }
}
