using Jellyfin.Plugin.KasuMeta.Providers;

namespace Jellyfin.Plugin.KasuMeta.Api.Models;

/// <summary>Response of <c>GET /KasuMeta/Status</c> (admins only): how the plugin is doing.</summary>
public sealed class KasuMetaStatus
{
    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the database schema version.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets a value indicating whether the full-text search index is in use (false: substring search).</summary>
    public bool SearchIndex { get; set; }

    /// <summary>Gets or sets a value indicating whether a TMDB key is set.</summary>
    public bool TmdbConfigured { get; set; }

    /// <summary>Gets or sets a value indicating whether AniList is turned on.</summary>
    public bool AniListEnabled { get; set; }

    /// <summary>Gets or sets how many items wait in the fetch queue (a backfill's progress).</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets the items KasuMeta knows, by the outcome of their last fetch.</summary>
    public ItemCounts Items { get; set; } = new(0, 0, 0, 0, 0);

    /// <summary>Gets or sets the newest failed fetches, newest first.</summary>
    public IReadOnlyList<FetchProblem> RecentErrors { get; set; } = [];

    /// <summary>
    /// Gets or sets each provider's counters since the server started (<c>tmdb</c>,
    /// <c>anilist</c>); a provider not used yet is absent.
    /// </summary>
    public IReadOnlyDictionary<string, ProviderCounters> Providers { get; set; } = new Dictionary<string, ProviderCounters>();
}

/// <summary>Items per fetch outcome.</summary>
/// <param name="Total">Every item with a fetch state.</param>
/// <param name="Ok">Fetched.</param>
/// <param name="Skipped">Nothing to fetch (no TMDB or AniList id, or unknown to them).</param>
/// <param name="Error">The last fetch failed; retried by "Refresh stale translations".</param>
/// <param name="Pending">Queued before and never finished (e.g. a restart mid-fetch).</param>
public sealed record ItemCounts(int Total, int Ok, int Skipped, int Error, int Pending);

/// <summary>One failed fetch.</summary>
/// <param name="ItemId">Dashless Jellyfin item id.</param>
/// <param name="Name">The item's name in the library; absent if it's gone.</param>
/// <param name="Type">Movie, Series, Season or Episode, when known.</param>
/// <param name="Error">What went wrong.</param>
/// <param name="LastFetch">When the fetch failed.</param>
public sealed record FetchProblem(string ItemId, string? Name, string? Type, string? Error, DateTimeOffset? LastFetch);
