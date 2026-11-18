using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Providers;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Providers.Tvdb;
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

    /// <summary>Nothing to fetch (no usable provider id, or no provider knows it).</summary>
    Skipped,

    /// <summary>The provider call failed; the error is in the fetch state.</summary>
    Failed,

    /// <summary>The item no longer exists (or isn't a kind KasuMeta handles); its rows were removed.</summary>
    Removed,
}

/// <summary>
/// Fetches one item's translations and artwork from the main providers the admin turned on
/// (TMDB, TVDB), merges them by priority, then lets AniList fill what they left empty for movies
/// and series with an AniList id. The order per field and locale is manual, then the main
/// providers by weight (<see cref="ProviderPriority"/>), then AniList. Keeps only locales in the
/// same fallback group as an enabled locale (all Spanish variants when es-MX is enabled), so the
/// server can fall back es-MX → es-ES without storing all ~40 TMDB languages.
/// </summary>
public sealed class MetadataFetcher
{
    /// <summary>
    /// Version of what a fetch stores: 1 = text, 2 = adds images, 3 = adds the item's kind (for
    /// search), 4 = adds AniList titles. Raise it whenever a fetch starts storing something new;
    /// the backfill then re-queues items fetched by older builds. (TVDB didn't raise it: it's
    /// off by default, and turning it on re-queues the library through the snapshot.)
    /// </summary>
    public const int FetchVersion = 4;

    /// <summary>The fetch-snapshot key holding <see cref="ProviderPriority.Fingerprint"/>.</summary>
    internal const string ProvidersSnapshotKey = "KasuMeta.Providers";

    private readonly ILibraryItemSource _library;
    private readonly ILocalizedMetadataStore _store;
    private readonly ITmdbClient _tmdb;
    private readonly ITvdbClient _tvdb;
    private readonly IAniListClient _aniList;
    private readonly IKasuMetaConfigProvider _config;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataFetcher> _logger;

    public MetadataFetcher(
        ILibraryItemSource library,
        ILocalizedMetadataStore store,
        ITmdbClient tmdb,
        ITvdbClient tvdb,
        IAniListClient aniList,
        IKasuMetaConfigProvider config,
        TimeProvider time,
        ILogger<MetadataFetcher> logger)
    {
        _library = library;
        _store = store;
        _tmdb = tmdb;
        _tvdb = tvdb;
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

        var config = _config.Current;
        var order = ProviderPriority.Order(config);
        var snapshot = Snapshot(item, config, order);
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

        // Each provider is tried on its own: one failing doesn't throw away another's text.
        var enabled = config.EnabledLocales;
        var fetched = false;
        var errors = new List<string>();
        var notes = new List<string>();
        if (order.Count == 0)
        {
            notes.Add("TMDB and TVDB are both turned off");
        }

        var outcomes = new List<(string Source, Outcome Outcome)>(order.Count);
        foreach (var source in order)
        {
            var outcome = source == MetadataSources.Tmdb
                ? await FetchTmdbAsync(item, enabled, cancellationToken).ConfigureAwait(false)
                : await FetchTvdbAsync(item, enabled, cancellationToken).ConfigureAwait(false);
            outcomes.Add((source, outcome));
            fetched |= outcome.Fields is not null;
            if (outcome.Note is { } note)
            {
                notes.Add(note);
            }

            if (outcome.Error is { } error)
            {
                errors.Add(error);
            }
        }

        await StoreMainAsync(itemId, outcomes, cancellationToken).ConfigureAwait(false);

        // AniList goes last, so that it fills only what the main providers (or an admin) left empty.
        var aniListId = config.AniListEnabled ? AniListId(item) : null;
        if (aniListId is null)
        {
            // AniList is off, or the item lost its id (re-identified): its AniList titles go.
            await _store.ReplaceProviderFieldsAsync(itemId, MetadataSources.AniList, [], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                if (await _aniList.GetMediaAsync(aniListId.Value, cancellationToken).ConfigureAwait(false) is { } translations)
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
    /// Merges lists of rows given in priority order: for each key, the row of the first list
    /// that has one. Rows within a list keep their order.
    /// </summary>
    internal static IReadOnlyList<T> Merge<T, TKey>(IEnumerable<IReadOnlyList<T>> byPriority, Func<T, TKey> key)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>();
        var merged = new List<T>();
        foreach (var rows in byPriority)
        {
            var taken = new HashSet<TKey>();
            foreach (var row in rows)
            {
                var k = key(row);
                if (!seen.Contains(k))
                {
                    merged.Add(row);
                    taken.Add(k);
                }
            }

            seen.UnionWith(taken);
        }

        return merged;
    }

    /// <summary>
    /// Turns TVDB's text into field rows. TVDB has one translation per language (<c>es</c>), where
    /// TMDB has one per country (<c>es-MX</c>, <c>es-ES</c>), so a bare-language value is stored
    /// under every enabled locale of that language (see <see cref="Spread"/>): that is what lets
    /// the priority choose between "TMDB's es-MX" and "TVDB's Spanish" at all.
    /// </summary>
    internal static IReadOnlyList<LocalizedField> ToTvdbFields(
        Guid itemId,
        IReadOnlyList<TvdbTranslation> translations,
        IReadOnlyCollection<string> enabledLocales)
    {
        var values = translations
            .SelectMany(t => new[]
            {
                (t.Locale, Key: MetadataFields.Name, Value: t.Title),
                (t.Locale, Key: MetadataFields.Overview, Value: t.Overview),
                (t.Locale, Key: MetadataFields.Tagline, Value: t.Tagline),
            })
            .Where(v => v.Value is not null)
            .Select(v => (v.Locale, v.Key, Value: v.Value!));
        return Spread(values, enabledLocales)
            .Select(v => new LocalizedField(itemId, v.Locale, v.Key, v.Value, MetadataSources.Tvdb))
            .ToList();
    }

    /// <summary>Turns TVDB's artwork into image rows, spread like <see cref="ToTvdbFields"/>.</summary>
    internal static IReadOnlyList<LocalizedImage> ToTvdbImages(
        Guid itemId,
        IReadOnlyList<TvdbImage> images,
        IReadOnlyCollection<string> enabledLocales)
    {
        var byKey = images.ToDictionary(i => (i.Locale, i.ImageType));
        return Spread(images.Select(i => (i.Locale, Key: i.ImageType, Value: i.Url)), enabledLocales)
            .Select(v =>
            {
                var image = byKey[(v.Source, v.Key)];
                return new LocalizedImage(itemId, v.Locale, v.Key, v.Value, MetadataSources.Tvdb, Width: image.Width, Height: image.Height);
            })
            .ToList();
    }

    /// <summary>
    /// Places per-language values on locales. A value whose locale is enabled stays there. A
    /// bare-language value (<c>es</c>) also goes to every enabled locale of the same language and
    /// fallback group (<c>es-MX</c>, <c>es-ES</c>) that has no value of its own for that key; if
    /// there is none, it stays on the bare language, where fallback finds it. Values outside the
    /// enabled locales' fallback groups are dropped.
    /// </summary>
    internal static IEnumerable<(string Locale, string Key, string Value, string Source)> Spread(
        IEnumerable<(string Locale, string Key, string Value)> values,
        IReadOnlyCollection<string> enabledLocales)
    {
        var groups = Groups(enabledLocales);
        var enabled = enabledLocales
            .Select(l => LocaleChain.TryNormalize(l, out var n) ? n : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var list = values.Where(v => groups.Contains(LocaleChain.FallbackGroup(v.Locale))).ToList();
        var own = list.Select(v => (v.Locale, v.Key)).ToHashSet();
        foreach (var value in list)
        {
            var targets = new List<string>();
            if (value.Locale == LocaleChain.Language(value.Locale))
            {
                targets.AddRange(enabled.Where(e =>
                    e != value.Locale
                    && LocaleChain.Language(e) == value.Locale
                    && LocaleChain.FallbackGroup(e) == LocaleChain.FallbackGroup(value.Locale)
                    && !own.Contains((e, value.Key))));
            }

            if (targets.Count == 0 || enabled.Contains(value.Locale, StringComparer.Ordinal))
            {
                targets.Add(value.Locale);
            }

            foreach (var locale in targets)
            {
                yield return (locale, value.Key, value.Value, value.Locale);
            }
        }
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

    /// <summary>
    /// The snapshot stored with the fetch state: the item's ids, plus the provider choice when it
    /// isn't the default, so that changing providers or their order re-fetches the item.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Snapshot(
        LibraryItemInfo item,
        PluginConfiguration config,
        IReadOnlyList<string> order)
    {
        var snapshot = item.FetchSnapshot(includeTvdb: order.Contains(MetadataSources.Tvdb, StringComparer.Ordinal));
        if (ProviderPriority.Fingerprint(config) is not { } fingerprint)
        {
            return snapshot;
        }

        return new Dictionary<string, string>(snapshot, StringComparer.Ordinal) { [ProvidersSnapshotKey] = fingerprint };
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

    private static int? PositiveId(IReadOnlyDictionary<string, string> ids, string provider) =>
        ids.TryGetValue(provider, out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0
            ? id
            : null;

    /// <summary>
    /// Writes the merge of the main providers: for each key, the value of the first provider in
    /// priority order that has one. A provider that failed this time keeps the unlocked rows it
    /// had, so a TVDB outage doesn't hand its fields to TMDB until the retry; a provider that
    /// was turned off, found nothing or no longer applies loses its rows.
    /// </summary>
    private async Task StoreMainAsync(
        Guid itemId,
        IReadOnlyList<(string Source, Outcome Outcome)> outcomes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalizedField> storedFields = [];
        IReadOnlyList<LocalizedImage> storedImages = [];
        if (outcomes.Any(o => o.Outcome.Error is not null))
        {
            storedFields = await _store.GetFieldsAsync([itemId], null, cancellationToken).ConfigureAwait(false);
            storedImages = await _store.GetImagesAsync([itemId], null, cancellationToken).ConfigureAwait(false);
        }

        var fields = Merge(
            outcomes.Select(o => o.Outcome.Fields
                                 ?? (o.Outcome.Error is not null
                                     ? storedFields.Where(r => r.Source == o.Source && !r.Locked).ToList()
                                     : [])),
            r => (r.Locale, r.Field));
        var images = Merge(
            outcomes.Select(o => o.Outcome.Images
                                 ?? (o.Outcome.Error is not null
                                     ? storedImages.Where(r => r.Source == o.Source && !r.Locked).ToList()
                                     : [])),
            r => (r.Locale, r.ImageType));
        await _store
            .ReplaceProvidersAsync(itemId, ProviderPriority.MainSources, fields, images, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Outcome> FetchTmdbAsync(LibraryItemInfo item, IReadOnlyCollection<string> enabled, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<TmdbTitle?>>? call = item.Kind switch
        {
            ItemKind.Movie when PositiveId(item.ProviderIds, ProviderNames.Tmdb) is { } movie =>
                ct => _tmdb.GetMovieAsync(movie, ct),
            ItemKind.Series when PositiveId(item.ProviderIds, ProviderNames.Tmdb) is { } series =>
                ct => _tmdb.GetSeriesAsync(series, ct),
            ItemKind.Season when PositiveId(item.SeriesProviderIds, ProviderNames.Tmdb) is { } series && item.SeasonNumber is { } season =>
                ct => _tmdb.GetSeasonAsync(series, season, ct),
            ItemKind.Episode when PositiveId(item.SeriesProviderIds, ProviderNames.Tmdb) is { } series
                                  && item.SeasonNumber is { } season
                                  && item.EpisodeNumber is { } episode =>
                ct => _tmdb.GetEpisodeAsync(series, season, episode, ct),
            _ => null,
        };
        if (call is null)
        {
            return Outcome.Skip(item.Kind is ItemKind.Season or ItemKind.Episode
                ? "No TMDB id on the series, or no season/episode number"
                : "No TMDB id");
        }

        try
        {
            return await call(cancellationToken).ConfigureAwait(false) is { } title
                ? Outcome.Rows(ToFields(item.Id, title.Translations, enabled), ToImages(item.Id, title.Images, enabled))
                : Outcome.Skip("TMDB has no such title (404)");
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            return Outcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// TVDB by the item's own TVDB id; else movies and series by their IMDb id, and seasons and
    /// episodes by the series' TVDB id and their numbers (one lookup request more).
    /// </summary>
    private async Task<Outcome> FetchTvdbAsync(LibraryItemInfo item, IReadOnlyCollection<string> enabled, CancellationToken cancellationToken)
    {
        var own = PositiveId(item.ProviderIds, ProviderNames.Tvdb);
        var series = PositiveId(item.SeriesProviderIds, ProviderNames.Tvdb);
        var imdb = item.ProviderIds.GetValueOrDefault(ProviderNames.Imdb);
        var applies = item.Kind switch
        {
            ItemKind.Movie or ItemKind.Series => own is not null || !string.IsNullOrWhiteSpace(imdb),
            ItemKind.Season => own is not null || (series is not null && item.SeasonNumber is not null),
            ItemKind.Episode => own is not null || (series is not null && item.SeasonNumber is not null && item.EpisodeNumber is not null),
            _ => false,
        };
        if (!applies)
        {
            return Outcome.Skip(item.Kind is ItemKind.Season or ItemKind.Episode
                ? "No TVDB id on the item or its series, or no season/episode number"
                : "No TVDB or IMDb id");
        }

        try
        {
            var id = own ?? await FindTvdbIdAsync(item, series, imdb, cancellationToken).ConfigureAwait(false);
            var call = id is not { } found
                ? Task.FromResult<TvdbTitle?>(null)
                : item.Kind switch
                {
                    ItemKind.Movie => _tvdb.GetMovieAsync(found, cancellationToken),
                    ItemKind.Series => _tvdb.GetSeriesAsync(found, cancellationToken),
                    ItemKind.Season => _tvdb.GetSeasonAsync(found, cancellationToken),
                    _ => _tvdb.GetEpisodeAsync(found, cancellationToken),
                };
            var title = await call.ConfigureAwait(false);
            return title is null
                ? Outcome.Skip("TVDB has no such title (404)")
                : Outcome.Rows(ToTvdbFields(item.Id, title.Translations, enabled), ToTvdbImages(item.Id, title.Images, enabled));
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            return Outcome.Failed(ex.Message.StartsWith("TVDB", StringComparison.Ordinal) ? ex.Message : "TVDB: " + ex.Message);
        }
    }

    /// <summary>The TVDB id of an item that has none of its own, looked up on TVDB; null if unknown there.</summary>
    private Task<int?> FindTvdbIdAsync(LibraryItemInfo item, int? seriesTvdb, string? imdb, CancellationToken cancellationToken) =>
        item.Kind switch
        {
            ItemKind.Movie => _tvdb.FindByImdbIdAsync(imdb!, series: false, cancellationToken),
            ItemKind.Series => _tvdb.FindByImdbIdAsync(imdb!, series: true, cancellationToken),
            ItemKind.Season => _tvdb.FindSeasonIdAsync(seriesTvdb!.Value, item.SeasonNumber!.Value, cancellationToken),
            _ => _tvdb.FindEpisodeIdAsync(seriesTvdb!.Value, item.SeasonNumber!.Value, item.EpisodeNumber!.Value, cancellationToken),
        };

    private Task SetStateAsync(
        LibraryItemInfo item,
        IReadOnlyDictionary<string, string> snapshot,
        FetchStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        _store.SetFetchStateAsync(
            new FetchState(item.Id, snapshot, _time.GetUtcNow(), status, error, FetchVersion, item.Kind),
            cancellationToken);

    /// <summary>What one main provider gave: rows (fetched), a note (nothing to fetch), or an error.</summary>
    private sealed record Outcome(
        IReadOnlyList<LocalizedField>? Fields,
        IReadOnlyList<LocalizedImage>? Images,
        string? Note,
        string? Error)
    {
        public static Outcome Rows(IReadOnlyList<LocalizedField> fields, IReadOnlyList<LocalizedImage> images) =>
            new(fields, images, null, null);

        public static Outcome Skip(string note) => new(null, null, note, null);

        public static Outcome Failed(string error) => new(null, null, null, error);
    }
}
