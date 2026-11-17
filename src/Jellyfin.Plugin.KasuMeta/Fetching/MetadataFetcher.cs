using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Fetching;

/// <summary>What a fetch did.</summary>
public enum FetchResult
{
    /// <summary>Translations were fetched and stored.</summary>
    Fetched,

    /// <summary>Not forced, and the provider ids match the last good fetch: no request sent.</summary>
    Unchanged,

    /// <summary>Nothing to fetch (no TMDB id, or TMDB doesn't know it).</summary>
    Skipped,

    /// <summary>The provider call failed; the error is in the fetch state.</summary>
    Failed,

    /// <summary>The item no longer exists (or isn't a kind KasuMeta handles); its rows were removed.</summary>
    Removed,
}

/// <summary>
/// Fetches one item's translations from TMDB and stores them. Keeps only locales in the same
/// fallback group as an enabled locale (all Spanish variants when es-MX is enabled), so the
/// server can fall back es-MX → es-ES without storing all ~40 TMDB languages.
/// </summary>
public sealed class MetadataFetcher
{
    private readonly ILibraryItemSource _library;
    private readonly ILocalizedMetadataStore _store;
    private readonly ITmdbClient _tmdb;
    private readonly IKasuMetaConfigProvider _config;
    private readonly TimeProvider _time;

    public MetadataFetcher(
        ILibraryItemSource library,
        ILocalizedMetadataStore store,
        ITmdbClient tmdb,
        IKasuMetaConfigProvider config,
        TimeProvider time)
    {
        _library = library;
        _store = store;
        _tmdb = tmdb;
        _config = config;
        _time = time;
    }

    /// <summary>Fetches one item; never throws for provider failures (they're recorded).</summary>
    public async Task<FetchResult> FetchAsync(Guid itemId, bool force, CancellationToken cancellationToken)
    {
        var item = _library.GetItem(itemId);
        if (item is null)
        {
            await _store.DeleteItemAsync(itemId, cancellationToken).ConfigureAwait(false);
            return FetchResult.Removed;
        }

        var snapshot = item.FetchSnapshot();
        if (!force)
        {
            var state = await _store.GetFetchStateAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (state is { Status: FetchStatus.Ok or FetchStatus.Skipped } && SameIds(state.ProviderIds, snapshot))
            {
                return FetchResult.Unchanged;
            }
        }

        IReadOnlyList<TmdbTranslation>? translations;
        try
        {
            var (call, skipReason) = PlanCall(item);
            if (call is null)
            {
                await SetStateAsync(itemId, snapshot, FetchStatus.Skipped, skipReason, cancellationToken).ConfigureAwait(false);
                return FetchResult.Skipped;
            }

            translations = await call(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            await SetStateAsync(itemId, snapshot, FetchStatus.Error, ex.Message, cancellationToken).ConfigureAwait(false);
            return FetchResult.Failed;
        }

        if (translations is null)
        {
            await SetStateAsync(itemId, snapshot, FetchStatus.Skipped, "TMDB has no such title (404)", cancellationToken)
                .ConfigureAwait(false);
            return FetchResult.Skipped;
        }

        var rows = ToFields(itemId, translations, _config.Current.EnabledLocales);
        await _store.ReplaceProviderFieldsAsync(itemId, MetadataSources.Tmdb, rows, cancellationToken).ConfigureAwait(false);
        await SetStateAsync(itemId, snapshot, FetchStatus.Ok, null, cancellationToken).ConfigureAwait(false);
        return FetchResult.Fetched;
    }

    /// <summary>Turns translations into field rows, keeping the enabled locales' fallback groups.</summary>
    internal static IReadOnlyList<LocalizedField> ToFields(
        Guid itemId,
        IReadOnlyList<TmdbTranslation> translations,
        IReadOnlyCollection<string> enabledLocales)
    {
        var groups = enabledLocales
            .Select(l => LocaleChain.TryNormalize(l, out var n) ? LocaleChain.FallbackGroup(n) : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var rows = new List<LocalizedField>();
        foreach (var t in translations)
        {
            if (!groups.Contains(LocaleChain.FallbackGroup(t.Locale)))
            {
                continue;
            }

            Add(MetadataFields.Name, t.Title);
            Add(MetadataFields.Overview, t.Overview);
            Add(MetadataFields.Tagline, t.Tagline);

            void Add(string field, string? value)
            {
                if (value is not null)
                {
                    rows.Add(new LocalizedField(itemId, t.Locale, field, value, MetadataSources.Tmdb));
                }
            }
        }

        return rows;
    }

    private static bool SameIds(IReadOnlyDictionary<string, string> stored, IReadOnlyDictionary<string, string> current) =>
        SqliteLocalizedMetadataStore.SerializeProviderIds(stored) == SqliteLocalizedMetadataStore.SerializeProviderIds(current);

    private static bool IsProviderFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => false,
        ProviderNotConfiguredException or ProviderAuthenticationException => true,
        HttpRequestException or JsonException or TaskCanceledException => true,
        _ => false,
    };

    private static int? TmdbId(IReadOnlyDictionary<string, string> ids) =>
        ids.TryGetValue(ProviderNames.Tmdb, out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0
            ? id
            : null;

    private (Func<CancellationToken, Task<IReadOnlyList<TmdbTranslation>?>>? Call, string? SkipReason) PlanCall(LibraryItemInfo item)
    {
        switch (item.Kind)
        {
            case ItemKind.Movie when TmdbId(item.ProviderIds) is { } movie:
                return (ct => _tmdb.GetMovieTranslationsAsync(movie, ct), null);
            case ItemKind.Series when TmdbId(item.ProviderIds) is { } series:
                return (ct => _tmdb.GetSeriesTranslationsAsync(series, ct), null);
            case ItemKind.Season when TmdbId(item.SeriesProviderIds) is { } series && item.SeasonNumber is { } season:
                return (ct => _tmdb.GetSeasonTranslationsAsync(series, season, ct), null);
            case ItemKind.Episode when TmdbId(item.SeriesProviderIds) is { } series
                                       && item.SeasonNumber is { } season
                                       && item.EpisodeNumber is { } episode:
                return (ct => _tmdb.GetEpisodeTranslationsAsync(series, season, episode, ct), null);
            case ItemKind.Season or ItemKind.Episode:
                return (null, "No TMDB id on the series, or no season/episode number");
            default:
                return (null, "No TMDB id");
        }
    }

    private Task SetStateAsync(
        Guid itemId,
        IReadOnlyDictionary<string, string> snapshot,
        FetchStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        _store.SetFetchStateAsync(new FetchState(itemId, snapshot, _time.GetUtcNow(), status, error), cancellationToken);
}
