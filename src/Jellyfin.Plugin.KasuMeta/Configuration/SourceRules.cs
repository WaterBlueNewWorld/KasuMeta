using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Configuration;

/// <summary>
/// One category of metadata and the providers it is taken from, in order: for each locale, the
/// first provider that has a value is the one stored; the next ones only fill what it lacks.
/// Serialized to XML by Jellyfin, so keep it public, settable and with defaults.
/// </summary>
public sealed class ProviderRule
{
    /// <summary>Gets or sets the category (one of <see cref="MetadataCategories"/>).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the providers (<c>tmdb</c>, <c>tvdb</c>, <c>anilist</c>), first choice first.</summary>
#pragma warning disable CA1819 // Jellyfin's XML serializer needs a settable array.
    public string[] Providers { get; set; } = [];
#pragma warning restore CA1819
}

/// <summary>The categories an admin picks providers for: text fields, list fields and image types.</summary>
public static class MetadataCategories
{
    public const string Name = MetadataFields.Name;
    public const string Overview = MetadataFields.Overview;
    public const string Tagline = MetadataFields.Tagline;
    public const string Genres = MetadataFields.Genres;
    public const string Tags = MetadataFields.Tags;
    public const string ImagePrefix = "image:";

    /// <summary>Gets every category, in the order the configuration page lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Name, Overview, Tagline, Genres, Tags,
        ImagePrefix + LocalizedImageTypes.Primary,
        ImagePrefix + LocalizedImageTypes.Logo,
        ImagePrefix + LocalizedImageTypes.Backdrop,
        ImagePrefix + LocalizedImageTypes.Thumb,
    ];

    /// <summary>Gets the category of an image type (<c>Primary</c> → <c>image:Primary</c>).</summary>
    public static string OfImage(string imageType) => ImagePrefix + imageType;

    /// <summary>
    /// Gets the providers that have data for a category, in the default order. Taglines come from
    /// TMDB and TVDB only; AniList has no artwork KasuMeta localizes; no provider has localized
    /// thumbs.
    /// </summary>
    public static IReadOnlyList<string> Supported(string category) => category switch
    {
        Name or Overview or Genres or Tags => [MetadataSources.Tmdb, MetadataSources.Tvdb, MetadataSources.AniList],
        Tagline => [MetadataSources.Tmdb, MetadataSources.Tvdb],
        _ when category == OfImage(LocalizedImageTypes.Thumb) => [],
        _ when category.StartsWith(ImagePrefix, StringComparison.Ordinal) => [MetadataSources.Tmdb, MetadataSources.Tvdb],
        _ => [],
    };
}

/// <summary>
/// Which provider each category of metadata comes from (<see cref="PluginConfiguration.ProviderRules"/>),
/// and which providers that makes KasuMeta query. A provider is queried when some category lists
/// it and it is set up: TMDB and TVDB need their API key, AniList needs none.
/// </summary>
public static class SourceRules
{
    /// <summary>Gets every provider KasuMeta knows.</summary>
    public static IReadOnlyList<string> AllSources { get; } = [MetadataSources.Tmdb, MetadataSources.Tvdb, MetadataSources.AniList];

    /// <summary>
    /// Gets each category's providers, in order, keeping only providers that support it. With no
    /// rules saved yet (an install upgraded from 1.1, or a fresh one), a category's list comes
    /// from the legacy settings: TMDB and TVDB as turned on, ordered by weight, then AniList
    /// where it has data and is turned on.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Effective(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var saved = (config.ProviderRules ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Category))
            .GroupBy(r => r.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var legacy = LegacyOrder(config);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var category in MetadataCategories.All)
        {
            var supported = MetadataCategories.Supported(category);
            IEnumerable<string> wanted = saved.TryGetValue(category, out var rule)
                ? (rule.Providers ?? []).Select(p => (p ?? string.Empty).Trim().ToLowerInvariant())
                : legacy;
            result[category] = wanted
                .Where(p => supported.Contains(p, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return result;
    }

    /// <summary>Whether a provider is set up: TMDB and TVDB need a key, AniList none.</summary>
    public static bool IsConfigured(PluginConfiguration config, string source)
    {
        ArgumentNullException.ThrowIfNull(config);
        return source switch
        {
            MetadataSources.Tmdb => !string.IsNullOrWhiteSpace(config.TmdbApiKey),
            MetadataSources.Tvdb => !string.IsNullOrWhiteSpace(config.TvdbApiKey),
            MetadataSources.AniList => true,
            _ => false,
        };
    }

    /// <summary>
    /// Gets the providers to query: listed by some category and set up, in the order they first
    /// appear going through the categories (titles first), so the usual first choice leads.
    /// </summary>
    public static IReadOnlyList<string> Usable(PluginConfiguration config)
    {
        var rules = Effective(config);
        return MetadataCategories.All
            .SelectMany(c => rules[c])
            .Distinct(StringComparer.Ordinal)
            .Where(s => IsConfigured(config, s))
            .ToList();
    }

    /// <summary>Gets each category's providers that are also usable (<see cref="Usable"/>), in order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Active(PluginConfiguration config)
    {
        var usable = Usable(config);
        return Effective(config).ToDictionary(
            p => p.Key,
            p => (IReadOnlyList<string>)p.Value.Where(s => usable.Contains(s, StringComparer.Ordinal)).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets a short text that changes exactly when what a fetch stores does (<see cref="Active"/>),
    /// or null for the default of TMDB, with AniList filling names. It goes into each item's
    /// fetch snapshot, so a change of providers makes every item count as changed.
    /// </summary>
    public static string? Fingerprint(PluginConfiguration config)
    {
        var text = Describe(Active(config));
        return text == DefaultDescription ? null : text;
    }

    /// <summary>The canonical text of a rule set: <c>name=tmdb&gt;tvdb;overview=…</c>, categories in <see cref="MetadataCategories.All"/> order.</summary>
    internal static string Describe(IReadOnlyDictionary<string, IReadOnlyList<string>> rules) =>
        string.Join(
            ';',
            MetadataCategories.All.Select(c => c + "=" + string.Join('>', rules.TryGetValue(c, out var list) ? list : [])));

    private static string DefaultDescription { get; } = Describe(
        MetadataCategories.All.ToDictionary(
            c => c,
            c => (IReadOnlyList<string>)MetadataCategories.Supported(c)
                .Where(s => s is MetadataSources.Tmdb or MetadataSources.AniList)
                .ToList(),
            StringComparer.Ordinal));

    /// <summary>The order the 1.1 settings gave: TMDB and TVDB as turned on, by weight (a tie keeps TMDB first), then AniList.</summary>
    private static List<string> LegacyOrder(PluginConfiguration config)
    {
        var main = new List<(string Source, int Weight, int Rank)>(2);
        if (config.TmdbEnabled)
        {
            main.Add((MetadataSources.Tmdb, Clamp(config.TmdbWeight), 0));
        }

        if (config.TvdbEnabled)
        {
            main.Add((MetadataSources.Tvdb, Clamp(config.TvdbWeight), 1));
        }

        var order = main.OrderByDescending(p => p.Weight).ThenBy(p => p.Rank).Select(p => p.Source).ToList();
        if (config.AniListEnabled)
        {
            order.Add(MetadataSources.AniList);
        }

        return order;
    }

    private static int Clamp(int weight) => Math.Clamp(weight, 0, 100);
}
