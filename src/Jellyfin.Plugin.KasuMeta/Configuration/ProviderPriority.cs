using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Configuration;

/// <summary>
/// Which of the main providers (TMDB, TVDB) an admin turned on, and in what order their values
/// win. For each field or image type and locale, the first provider in <see cref="Order"/> that
/// has a value is the one stored; the others only fill what it lacks. AniList isn't one of them:
/// it only ever fills what no other source holds.
/// </summary>
public static class ProviderPriority
{
    /// <summary>The lowest weight the configuration page accepts.</summary>
    public const int MinWeight = 0;

    /// <summary>The highest weight the configuration page accepts.</summary>
    public const int MaxWeight = 100;

    /// <summary>TMDB's weight until an admin changes it.</summary>
    public const int DefaultTmdbWeight = 100;

    /// <summary>TVDB's weight until an admin changes it.</summary>
    public const int DefaultTvdbWeight = 50;

    /// <summary>Gets the main providers, whether or not they are turned on.</summary>
    public static IReadOnlyList<string> MainSources { get; } = [MetadataSources.Tmdb, MetadataSources.Tvdb];

    /// <summary>
    /// Gets the turned-on main providers, highest weight first. Weights are clamped to
    /// <see cref="MinWeight"/>–<see cref="MaxWeight"/>; a tie keeps TMDB first.
    /// </summary>
    public static IReadOnlyList<string> Order(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var enabled = new List<(string Source, int Weight, int Rank)>(2);
        if (config.TmdbEnabled)
        {
            enabled.Add((MetadataSources.Tmdb, Clamp(config.TmdbWeight), 0));
        }

        if (config.TvdbEnabled)
        {
            enabled.Add((MetadataSources.Tvdb, Clamp(config.TvdbWeight), 1));
        }

        return enabled
            .OrderByDescending(p => p.Weight)
            .ThenBy(p => p.Rank)
            .Select(p => p.Source)
            .ToList();
    }

    /// <summary>
    /// Gets a short text that changes exactly when <see cref="Order"/> does (<c>tvdb&gt;tmdb</c>),
    /// or null for the default of TMDB alone. It goes into each item's fetch snapshot, so a
    /// change of providers or of which one wins makes every item count as changed, while an
    /// install that never touched these settings keeps the snapshots it already has.
    /// </summary>
    public static string? Fingerprint(PluginConfiguration config)
    {
        var order = Order(config);
        return order is [MetadataSources.Tmdb] ? null : string.Join('>', order);
    }

    private static int Clamp(int weight) => Math.Clamp(weight, MinWeight, MaxWeight);
}
