using Jellyfin.Plugin.KasuMeta.Configuration;

namespace Jellyfin.Plugin.KasuMeta.Tests.Configuration;

public class SourceRulesTests
{
    [Fact]
    public void A_fresh_install_uses_tmdb_with_anilist_filling_text_and_terms()
    {
        var rules = SourceRules.Effective(new PluginConfiguration());

        Assert.Equal(["tmdb", "anilist"], rules[MetadataCategories.Name]);
        Assert.Equal(["tmdb"], rules[MetadataCategories.Tagline]); // AniList has no taglines
        Assert.Equal(["tmdb", "anilist"], rules[MetadataCategories.Tags]);
        Assert.Equal(["tmdb"], rules[MetadataCategories.OfImage("Logo")]);
        Assert.Empty(rules[MetadataCategories.OfImage("Thumb")]);
        Assert.Equal(MetadataCategories.All, rules.Keys.ToList());
    }

    [Fact]
    public void Saved_rules_win_and_unknown_or_unsupported_entries_are_dropped()
    {
        var config = new PluginConfiguration
        {
            TvdbEnabled = false,
            ProviderRules =
            [
                new ProviderRule { Category = "Overview", Providers = ["TVDB", "tmdb", "tvdb", "imdb"] },
                new ProviderRule { Category = "image:Primary", Providers = ["anilist", "tmdb"] },
                new ProviderRule { Category = "tagline", Providers = [] },
                new ProviderRule { Category = "studio", Providers = ["tmdb"] },
            ],
        };

        var rules = SourceRules.Effective(config);

        Assert.Equal(["tvdb", "tmdb"], rules[MetadataCategories.Overview]);
        Assert.Equal(["tmdb"], rules[MetadataCategories.OfImage("Primary")]);
        Assert.Empty(rules[MetadataCategories.Tagline]);
        Assert.Equal(["tmdb", "anilist"], rules[MetadataCategories.Name]); // not saved: legacy settings
        Assert.False(rules.ContainsKey("studio"));
    }

    [Fact]
    public void Only_listed_providers_with_their_key_are_used()
    {
        var config = new PluginConfiguration
        {
            TmdbApiKey = string.Empty,
            TvdbApiKey = "key",
            ProviderRules = [new ProviderRule { Category = "name", Providers = ["tvdb", "tmdb"] }],
        };

        Assert.Equal(["tvdb", "anilist"], SourceRules.Usable(config));
        Assert.Equal(["tvdb"], SourceRules.Active(config)[MetadataCategories.Name]);
        Assert.Empty(SourceRules.Active(config)[MetadataCategories.OfImage("Primary")]); // TMDB has no key
    }

    [Fact]
    public void The_default_has_no_fingerprint_and_any_change_has_one()
    {
        var config = new PluginConfiguration { TmdbApiKey = "key" };
        Assert.Null(SourceRules.Fingerprint(config));

        config.ProviderRules = [new ProviderRule { Category = "overview", Providers = ["anilist", "tmdb"] }];
        Assert.StartsWith("name=tmdb>anilist;overview=anilist>tmdb;", SourceRules.Fingerprint(config), StringComparison.Ordinal);

        // A key-less provider doesn't count, so setting its key is a change.
        config.ProviderRules = [new ProviderRule { Category = "overview", Providers = ["tvdb", "tmdb", "anilist"] }];
        Assert.Null(SourceRules.Fingerprint(config));
        config.TvdbApiKey = "key";
        Assert.NotNull(SourceRules.Fingerprint(config));
    }
}
