using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Storage;
using Microsoft.Extensions.Logging;

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
/// Fetches one item's translations from TMDB, then from AniList for movies and series with an
/// AniList id, and stores them. AniList only fills what no other source holds, so the order per
/// field and locale is manual, TMDB, AniList. Keeps only locales in the same fallback group as an
/// enabled locale (all Spanish variants when es-MX is enabled), so the server can fall back
/// es-MX → es-ES without storing all ~40 TMDB languages.
/// </summary>
public sealed class MetadataFetcher
{
    /// <summary>
    /// Version of what a fetch stores: 1 = text, 2 = adds images, 3 = adds the item's kind (for
    /// search), 4 = adds AniList titles. Raise it whenever a fetch starts storing something new;
    /// the backfill then re-queues items fetched by older builds.
    /// </summary>
    public const int FetchVersion = 4;

    private readonly ILibraryItemSource _library;
    private readonly ILocalizedMetadataStore _store;
    private readonly ITmdbClient _tmdb;
    private readonly IAniListClient _aniList;
    private readonly IKasuMetaConfigProvider _config;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataFetcher> _logger;

    public MetadataFetcher(
        ILibraryItemSource library,
        ILocalizedMetadataStore store,
        ITmdbClient tmdb,
        IAniListClient aniList,
        IKasuMetaConfigProvider config,
        TimeProvider time,
        ILogger<MetadataFetcher> logger)
    {
        _library = library;
        _store = store;
        _tmdb = tmdb;
        _aniList = aniList;
        _config = config;
        _time = time;
        _logger = logger;
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
            if (state is { Status: FetchStatus.Ok or FetchStatus.Skipped }
                && state.FetchVersion >= FetchVersion
                && SameIds(state.ProviderIds, snapshot))
            {
                return FetchResult.Unchanged;
            }
        }

        var (tmdbCall, tmdbSkipReason) = PlanCall(item);
        var aniListId = _config.Current.AniListEnabled ? AniListId(item) : null;
        if (aniListId is null)
        {
            // AniList is off, or the item lost its id (re-identified): its AniList titles go.
            await _store.ReplaceProviderFieldsAsync(itemId, MetadataSources.AniList, [], cancellationToken).ConfigureAwait(false);
        }

        if (tmdbCall is null && aniListId is null)
        {
            await SetStateAsync(item, snapshot, FetchStatus.Skipped, tmdbSkipReason, cancellationToken).ConfigureAwait(false);
            return FetchResult.Skipped;
        }

        // Each provider is tried on its own: one failing doesn't throw away the other's text.
        // TMDB goes first so that AniList can fill only what TMDB (or an admin) left empty.
        var enabled = _config.Current.EnabledLocales;
        var fetched = false;
        var errors = new List<string>();
        var notes = new List<string>();
        if (tmdbCall is null)
        {
            notes.Add(tmdbSkipReason!);
        }
        else
        {
            try
            {
                if (await tmdbCall(cancellationToken).ConfigureAwait(false) is { } title)
                {
                    await _store
                        .ReplaceProviderFieldsAsync(itemId, MetadataSources.Tmdb, ToFields(itemId, title.Translations, enabled), cancellationToken)
                        .ConfigureAwait(false);
                    await _store
                        .ReplaceProviderImagesAsync(itemId, MetadataSources.Tmdb, ToImages(itemId, title.Images, enabled), cancellationToken)
                        .ConfigureAwait(false);
                    fetched = true;
                }
                else
                {
                    notes.Add("TMDB has no such title (404)");
                }
            }
            catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
            {
                errors.Add(ex.Message);
            }
        }

        if (aniListId is { } mediaId)
        {
            try
            {
                if (await _aniList.GetMediaAsync(mediaId, cancellationToken).ConfigureAwait(false) is { } translations)
                {
                    var held = (await _store.GetFieldsAsync([itemId], null, cancellationToken).ConfigureAwait(false))
                        .Where(r => r.Source != MetadataSources.AniList)
                        .Select(r => (r.Locale, r.Field))
                        .ToHashSet();
                    var rows = ToAniListFields(itemId, translations, enabled)
                        .Where(r => !held.Contains((r.Locale, r.Field)))
                        .ToList();
                    await _store
                        .ReplaceProviderFieldsAsync(itemId, MetadataSources.AniList, rows, cancellationToken)
                        .ConfigureAwait(false);
                    fetched = true;
                }
                else
                {
                    notes.Add("AniList has no such title (404)");
                }
            }
            catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
            {
                errors.Add("AniList: " + ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            var error = string.Join("; ", errors);
            FetchLog.ProviderFailed(_logger, item.Name, itemId, error);
            await SetStateAsync(item, snapshot, FetchStatus.Error, error, cancellationToken).ConfigureAwait(false);
            return FetchResult.Failed;
        }

        if (!fetched)
        {
            await SetStateAsync(item, snapshot, FetchStatus.Skipped, string.Join("; ", notes), cancellationToken).ConfigureAwait(false);
            return FetchResult.Skipped;
        }

        await SetStateAsync(item, snapshot, FetchStatus.Ok, null, cancellationToken).ConfigureAwait(false);
        return FetchResult.Fetched;
    }

    /// <summary>
    /// Turns AniList's text into field rows, keeping the enabled locales' fallback groups, plus
    /// romanized titles (<c>ja-Latn</c>, …) whether enabled or not: people type an anime's romaji
    /// name into search whatever language they read. Romanized locales are a fallback group of
    /// their own, so storing them never changes what another locale is served.
    /// </summary>
    internal static IReadOnlyList<LocalizedField> ToAniListFields(
        Guid itemId,
        IReadOnlyList<AniListTranslation> translations,
        IReadOnlyCollection<string> enabledLocales)
    {
        var groups = Groups(enabledLocales);
        var rows = new List<LocalizedField>();
        foreach (var t in translations.Where(t => groups.Contains(LocaleChain.FallbackGroup(t.Locale)) || IsRomanized(t.Locale)))
        {
            if (t.Title is not null)
            {
                rows.Add(new LocalizedField(itemId, t.Locale, MetadataFields.Name, t.Title, MetadataSources.AniList));
            }

            if (t.Overview is not null)
            {
                rows.Add(new LocalizedField(itemId, t.Locale, MetadataFields.Overview, t.Overview, MetadataSources.AniList));
            }
        }

        return rows;
    }

    /// <summary>Whether a locale is a romanization with a fallback group of its own (<c>ja-Latn</c>).</summary>
    private static bool IsRomanized(string locale) =>
        LocaleChain.FallbackGroup(locale).EndsWith("-Latn", StringComparison.Ordinal);

    /// <summary>
    /// The item's AniList media id. Only movies and series: AniList has no episodes, and treats
    /// each season as a title of its own, which Jellyfin's seasons don't map to.
    /// </summary>
    private static int? AniListId(LibraryItemInfo item) =>
        item.Kind is ItemKind.Movie or ItemKind.Series
        && item.ProviderIds.TryGetValue(ProviderNames.AniList, out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0
            ? id
            : null;

    /// <summary>Turns images into rows, keeping the enabled locales' fallback groups.</summary>
    internal static IReadOnlyList<LocalizedImage> ToImages(
        Guid itemId,
        IReadOnlyList<TmdbImage> images,
        IReadOnlyCollection<string> enabledLocales)
    {
        var groups = Groups(enabledLocales);
        return images
            .Where(i => groups.Contains(LocaleChain.FallbackGroup(i.Locale)))
            .Select(i => new LocalizedImage(itemId, i.Locale, i.ImageType, i.Url, MetadataSources.Tmdb, Width: i.Width, Height: i.Height))
            .ToList();
    }

    /// <summary>Turns translations into field rows, keeping the enabled locales' fallback groups.</summary>
    internal static IReadOnlyList<LocalizedField> ToFields(
        Guid itemId,
        IReadOnlyList<TmdbTranslation> translations,
        IReadOnlyCollection<string> enabledLocales)
    {
        var groups = Groups(enabledLocales);

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

    private static HashSet<string> Groups(IReadOnlyCollection<string> enabledLocales) =>
        enabledLocales
            .Select(l => LocaleChain.TryNormalize(l, out var n) ? LocaleChain.FallbackGroup(n) : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

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

    private (Func<CancellationToken, Task<TmdbTitle?>>? Call, string? SkipReason) PlanCall(LibraryItemInfo item)
    {
        switch (item.Kind)
        {
            case ItemKind.Movie when TmdbId(item.ProviderIds) is { } movie:
                return (ct => _tmdb.GetMovieAsync(movie, ct), null);
            case ItemKind.Series when TmdbId(item.ProviderIds) is { } series:
                return (ct => _tmdb.GetSeriesAsync(series, ct), null);
            case ItemKind.Season when TmdbId(item.SeriesProviderIds) is { } series && item.SeasonNumber is { } season:
                return (ct => _tmdb.GetSeasonAsync(series, season, ct), null);
            case ItemKind.Episode when TmdbId(item.SeriesProviderIds) is { } series
                                       && item.SeasonNumber is { } season
                                       && item.EpisodeNumber is { } episode:
                return (ct => _tmdb.GetEpisodeAsync(series, season, episode, ct), null);
            case ItemKind.Season or ItemKind.Episode:
                return (null, "No TMDB id on the series, or no season/episode number");
            default:
                return (null, "No TMDB id");
        }
    }

    private Task SetStateAsync(
        LibraryItemInfo item,
        IReadOnlyDictionary<string, string> snapshot,
        FetchStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        _store.SetFetchStateAsync(
            new FetchState(item.Id, snapshot, _time.GetUtcNow(), status, error, FetchVersion, item.Kind),
            cancellationToken);
}
