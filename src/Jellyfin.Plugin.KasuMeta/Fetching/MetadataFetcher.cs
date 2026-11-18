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
/// Fetches one item's translations, genres, tags and artwork from every provider some category
/// lists and that is set up (<see cref="SourceRules.Usable"/>), then merges them category by
/// category: for each field (or image type) and locale, the value of the first provider in that
/// category's list that has one (<see cref="SourceRules.Active"/>); the next ones fill what it
/// lacks. Manual locked values always stay.
/// </summary>
/// <remarks>
/// Keeps only locales in the same fallback group as an enabled locale (all Spanish variants when
/// es-MX is enabled), so the server can fall back es-MX → es-ES without storing all ~40 TMDB
/// languages; plus the item's original language, the last-resort fallback; plus romanized titles
/// for search; plus English genres and tags, the terms the admin's dictionary translates.
/// </remarks>
public sealed class MetadataFetcher
{
    /// <summary>
    /// Version of what a fetch stores: 1 = text, 2 = adds images, 3 = adds the item's kind (for
    /// search), 4 = adds AniList titles, 5 = adds genres, tags, the original language and its
    /// values. Raise it whenever a fetch starts storing something new; the backfill then
    /// re-queues items fetched by older builds.
    /// </summary>
    public const int FetchVersion = 5;

    /// <summary>The fetch-snapshot key holding <see cref="SourceRules.Fingerprint"/>.</summary>
    internal const string ProvidersSnapshotKey = "KasuMeta.Providers";

    /// <summary>Where provider genres and tags in English are stored: the dictionary's input.</summary>
    internal const string BaseTermsLocale = "en";

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
        var rules = SourceRules.Active(config);
        var usable = SourceRules.Usable(config);
        var snapshot = Snapshot(item, config, usable);
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

        // Each provider is tried on its own: one failing doesn't throw away another's values.
        var enabled = config.EnabledLocales;
        var errors = new List<string>();
        var notes = new List<string>();
        if (usable.Count == 0)
        {
            notes.Add("No provider that a category lists is set up");
        }

        TmdbTitle? tmdb = null;
        TvdbTitle? tvdb = null;
        IReadOnlyList<AniListTranslation>? aniList = null;
        var outcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        foreach (var source in usable)
        {
            Outcome outcome;
            switch (source)
            {
                case MetadataSources.Tmdb:
                    (outcome, tmdb) = await FetchTmdbAsync(item, cancellationToken).ConfigureAwait(false);
                    break;
                case MetadataSources.Tvdb:
                    (outcome, tvdb) = await FetchTvdbAsync(item, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    (outcome, aniList) = await FetchAniListAsync(item, cancellationToken).ConfigureAwait(false);
                    break;
            }

            outcomes[source] = outcome;
            if (outcome.Note is { } note)
            {
                notes.Add(note);
            }

            if (outcome.Error is { } error)
            {
                errors.Add(error);
            }
        }

        // The original language is known only now, and decides which locales are kept.
        var original = item.OriginalLanguage ?? tmdb?.OriginalLanguage ?? tvdb?.OriginalLanguage;
        var rows = new Dictionary<string, (IReadOnlyList<LocalizedField> Fields, IReadOnlyList<LocalizedImage> Images)>(StringComparer.Ordinal);
        if (tmdb is not null)
        {
            try
            {
                var genres = rules[MetadataCategories.Genres].Contains(MetadataSources.Tmdb, StringComparer.Ordinal)
                    ? await TmdbGenreFieldsAsync(item, tmdb, enabled, original, cancellationToken).ConfigureAwait(false)
                    : [];
                rows[MetadataSources.Tmdb] = (
                    ToFields(item.Id, tmdb.Translations, enabled, original)
                        .Concat(genres)
                        .Concat(BaseTerms(item.Id, MetadataFields.Tags, tmdb.Keywords, MetadataSources.Tmdb))
                        .ToList(),
                    ToImages(item.Id, tmdb.Images, enabled, original));
            }
            catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
            {
                outcomes[MetadataSources.Tmdb] = Outcome.Failed(ex.Message);
                errors.Add(ex.Message);
            }
        }

        if (tvdb is not null)
        {
            rows[MetadataSources.Tvdb] = (
                ToTvdbFields(item.Id, tvdb.Translations, enabled, original)
                    .Concat(BaseTerms(item.Id, MetadataFields.Genres, tvdb.Genres, MetadataSources.Tvdb))
                    .Concat(BaseTerms(item.Id, MetadataFields.Tags, tvdb.Tags, MetadataSources.Tvdb))
                    .ToList(),
                ToTvdbImages(item.Id, tvdb.Images, enabled, original));
        }

        if (aniList is not null)
        {
            rows[MetadataSources.AniList] = (ToAniListFields(item.Id, aniList, enabled, original), []);
        }

        await StoreAsync(itemId, rules, outcomes, rows, cancellationToken).ConfigureAwait(false);
        var fetched = rows.Count > 0;

        if (errors.Count > 0)
        {
            var error = string.Join("; ", errors);
            FetchLog.ProviderFailed(_logger, item.Name, itemId, error);
            await SetStateAsync(item, snapshot, FetchStatus.Error, error, original, cancellationToken).ConfigureAwait(false);
            return FetchResult.Failed;
        }

        if (!fetched)
        {
            await SetStateAsync(item, snapshot, FetchStatus.Skipped, string.Join("; ", notes), original, cancellationToken)
                .ConfigureAwait(false);
            return FetchResult.Skipped;
        }

        await SetStateAsync(item, snapshot, FetchStatus.Ok, null, original, cancellationToken).ConfigureAwait(false);
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
    /// Merges every provider's rows category by category: for each category, the providers in
    /// that category's order, each filling the (locale, key) pairs the ones before it lack.
    /// </summary>
    internal static (IReadOnlyList<LocalizedField> Fields, IReadOnlyList<LocalizedImage> Images) MergeByCategory(
        IReadOnlyDictionary<string, IReadOnlyList<string>> rules,
        IReadOnlyDictionary<string, (IReadOnlyList<LocalizedField> Fields, IReadOnlyList<LocalizedImage> Images)> rowsBySource)
    {
        var fields = new List<LocalizedField>();
        var images = new List<LocalizedImage>();
        foreach (var (category, order) in rules)
        {
            var sources = order.Where(rowsBySource.ContainsKey).ToList();
            if (category.StartsWith(MetadataCategories.ImagePrefix, StringComparison.Ordinal))
            {
                var type = category[MetadataCategories.ImagePrefix.Length..];
                images.AddRange(Merge(
                    sources.Select(s => (IReadOnlyList<LocalizedImage>)rowsBySource[s].Images.Where(i => i.ImageType == type).ToList()),
                    i => i.Locale));
            }
            else
            {
                fields.AddRange(Merge(
                    sources.Select(s => (IReadOnlyList<LocalizedField>)rowsBySource[s].Fields.Where(f => f.Field == category).ToList()),
                    f => f.Locale));
            }
        }

        return (fields, images);
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
        IReadOnlyCollection<string> enabledLocales,
        string? originalLanguage = null)
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
        return Spread(values, enabledLocales, originalLanguage)
            .Select(v => new LocalizedField(itemId, v.Locale, v.Key, v.Value, MetadataSources.Tvdb))
            .ToList();
    }

    /// <summary>Turns TVDB's artwork into image rows, spread like <see cref="ToTvdbFields"/>.</summary>
    internal static IReadOnlyList<LocalizedImage> ToTvdbImages(
        Guid itemId,
        IReadOnlyList<TvdbImage> images,
        IReadOnlyCollection<string> enabledLocales,
        string? originalLanguage = null)
    {
        var byKey = images.ToDictionary(i => (i.Locale, i.ImageType));
        return Spread(images.Select(i => (i.Locale, Key: i.ImageType, Value: i.Url)), enabledLocales, originalLanguage)
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
    /// enabled locales' fallback groups (and the original language's) are dropped.
    /// </summary>
    internal static IEnumerable<(string Locale, string Key, string Value, string Source)> Spread(
        IEnumerable<(string Locale, string Key, string Value)> values,
        IReadOnlyCollection<string> enabledLocales,
        string? originalLanguage = null)
    {
        var groups = Groups(enabledLocales, originalLanguage);
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
    /// Turns AniList's text into field rows, keeping the enabled locales' fallback groups (and the
    /// original language's), plus romanized titles (<c>ja-Latn</c>, …) whether enabled or not:
    /// people type an anime's romaji name into search whatever language they read. Romanized
    /// locales are a fallback group of their own, so storing them never changes what another
    /// locale is served. Genres and tags (English) become base terms.
    /// </summary>
    internal static IReadOnlyList<LocalizedField> ToAniListFields(
        Guid itemId,
        IReadOnlyList<AniListTranslation> translations,
        IReadOnlyCollection<string> enabledLocales,
        string? originalLanguage = null)
    {
        var groups = Groups(enabledLocales, originalLanguage);
        var rows = new List<LocalizedField>();
        foreach (var t in translations)
        {
            rows.AddRange(BaseTerms(itemId, MetadataFields.Genres, t.Genres, MetadataSources.AniList));
            rows.AddRange(BaseTerms(itemId, MetadataFields.Tags, t.Tags, MetadataSources.AniList));
            if (!groups.Contains(LocaleChain.FallbackGroup(t.Locale)) && !IsRomanized(t.Locale))
            {
                continue;
            }

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

    /// <summary>Turns images into rows, keeping the enabled locales' fallback groups (and the original language's).</summary>
    internal static IReadOnlyList<LocalizedImage> ToImages(
        Guid itemId,
        IReadOnlyList<TmdbImage> images,
        IReadOnlyCollection<string> enabledLocales,
        string? originalLanguage = null)
    {
        var groups = Groups(enabledLocales, originalLanguage);
        return images
            .Where(i => groups.Contains(LocaleChain.FallbackGroup(i.Locale)))
            .Select(i => new LocalizedImage(itemId, i.Locale, i.ImageType, i.Url, MetadataSources.Tmdb, Width: i.Width, Height: i.Height))
            .ToList();
    }

    /// <summary>Turns translations into field rows, keeping the enabled locales' fallback groups (and the original language's).</summary>
    internal static IReadOnlyList<LocalizedField> ToFields(
        Guid itemId,
        IReadOnlyList<TmdbTranslation> translations,
        IReadOnlyCollection<string> enabledLocales,
        string? originalLanguage = null)
    {
        var groups = Groups(enabledLocales, originalLanguage);

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
    /// The locales TMDB's genre names are asked in: the enabled ones, and the original language
    /// when no enabled locale shares its fallback group.
    /// </summary>
    internal static IReadOnlyList<string> GenreLocales(IReadOnlyCollection<string> enabledLocales, string? originalLanguage)
    {
        var locales = enabledLocales
            .Select(l => LocaleChain.TryNormalize(l, out var n) ? n : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (LocaleChain.TryNormalize(originalLanguage, out var original)
            && !locales.Any(l => LocaleChain.FallbackGroup(l) == LocaleChain.FallbackGroup(original)))
        {
            locales.Add(original);
        }

        return locales;
    }

    /// <summary>
    /// The snapshot stored with the fetch state: the item's ids, plus the provider choice when it
    /// isn't the default, so that changing providers or their order re-fetches the item.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Snapshot(
        LibraryItemInfo item,
        PluginConfiguration config,
        IReadOnlyList<string> usable)
    {
        var snapshot = item.FetchSnapshot(includeTvdb: usable.Contains(MetadataSources.Tvdb, StringComparer.Ordinal));
        if (SourceRules.Fingerprint(config) is not { } fingerprint)
        {
            return snapshot;
        }

        return new Dictionary<string, string>(snapshot, StringComparer.Ordinal) { [ProvidersSnapshotKey] = fingerprint };
    }

    /// <summary>A list of English terms as one base-terms row (<see cref="BaseTermsLocale"/>), or none.</summary>
    private static IEnumerable<LocalizedField> BaseTerms(Guid itemId, string field, IReadOnlyList<string>? terms, string source) =>
        ListValues.Serialize(terms ?? []) is { } json
            ? [new LocalizedField(itemId, BaseTermsLocale, field, json, source)]
            : [];

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

    private static HashSet<string> Groups(IReadOnlyCollection<string> enabledLocales, string? originalLanguage = null)
    {
        var groups = enabledLocales
            .Select(l => LocaleChain.TryNormalize(l, out var n) ? LocaleChain.FallbackGroup(n) : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (LocaleChain.TryNormalize(originalLanguage, out var original))
        {
            groups.Add(LocaleChain.FallbackGroup(original));
        }

        return groups;
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

    private static int? PositiveId(IReadOnlyDictionary<string, string> ids, string provider) =>
        ids.TryGetValue(provider, out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0
            ? id
            : null;

    /// <summary>
    /// Writes the per-category merge of every provider. A provider that failed this time keeps
    /// the unlocked rows it had, so a TVDB outage doesn't hand its fields to TMDB until the retry;
    /// a provider that is no longer listed or set up, found nothing or no longer applies loses
    /// its rows.
    /// </summary>
    private async Task StoreAsync(
        Guid itemId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> rules,
        IReadOnlyDictionary<string, Outcome> outcomes,
        Dictionary<string, (IReadOnlyList<LocalizedField> Fields, IReadOnlyList<LocalizedImage> Images)> rows,
        CancellationToken cancellationToken)
    {
        var failed = outcomes.Where(o => o.Value.Error is not null).Select(o => o.Key).ToList();
        IReadOnlyList<LocalizedField> storedFields = [];
        if (failed.Count > 0 || rows.ContainsKey(MetadataSources.AniList))
        {
            storedFields = await _store.GetFieldsAsync([itemId], null, cancellationToken).ConfigureAwait(false);
        }

        if (failed.Count > 0)
        {
            var storedImages = await _store.GetImagesAsync([itemId], null, cancellationToken).ConfigureAwait(false);
            foreach (var source in failed)
            {
                rows[source] = (
                    storedFields.Where(r => r.Source == source && !r.Locked).ToList(),
                    storedImages.Where(r => r.Source == source && !r.Locked && !r.HasFile).ToList());
            }
        }

        var (fields, images) = MergeByCategory(rules, rows);

        // AniList only ever fills: it never replaces an admin's value, even an unlocked one.
        var manual = storedFields
            .Where(r => r.Source == MetadataSources.Manual)
            .Select(r => (r.Locale, r.Field))
            .ToHashSet();
        fields = fields.Where(f => f.Source != MetadataSources.AniList || !manual.Contains((f.Locale, f.Field))).ToList();
        await _store
            .ReplaceProvidersAsync(itemId, SourceRules.AllSources, fields, images, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// TMDB's genres of a movie or series in each enabled locale (and the original language),
    /// from its per-language genre lists, plus the English names as base terms.
    /// </summary>
    private async Task<IReadOnlyList<LocalizedField>> TmdbGenreFieldsAsync(
        LibraryItemInfo item,
        TmdbTitle title,
        IReadOnlyCollection<string> enabled,
        string? original,
        CancellationToken cancellationToken)
    {
        if (title.Genres is not { Count: > 0 } genres || item.Kind is not (ItemKind.Movie or ItemKind.Series))
        {
            return [];
        }

        var rows = BaseTerms(item.Id, MetadataFields.Genres, genres.Select(g => g.Name).ToList(), MetadataSources.Tmdb).ToList();
        foreach (var locale in GenreLocales(enabled, original))
        {
            var names = await _tmdb.GetGenreNamesAsync(item.Kind == ItemKind.Series, locale, cancellationToken).ConfigureAwait(false);
            var localized = genres.Select(g => names.GetValueOrDefault(g.Id)).ToList();
            if (localized.Any(n => n is null))
            {
                // A genre TMDB doesn't name in this locale: leave the locale to fallback rather
                // than serve a partial list.
                continue;
            }

            if (ListValues.Serialize(localized) is { } json)
            {
                rows.Add(new LocalizedField(item.Id, locale, MetadataFields.Genres, json, MetadataSources.Tmdb));
            }
        }

        return rows;
    }

    private async Task<(Outcome Outcome, TmdbTitle? Title)> FetchTmdbAsync(LibraryItemInfo item, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string>? extra = item.OriginalLanguage is { } language ? [language] : null;
        Func<CancellationToken, Task<TmdbTitle?>>? call = item.Kind switch
        {
            ItemKind.Movie when PositiveId(item.ProviderIds, ProviderNames.Tmdb) is { } movie =>
                ct => _tmdb.GetMovieAsync(movie, ct, extra),
            ItemKind.Series when PositiveId(item.ProviderIds, ProviderNames.Tmdb) is { } series =>
                ct => _tmdb.GetSeriesAsync(series, ct, extra),
            ItemKind.Season when PositiveId(item.SeriesProviderIds, ProviderNames.Tmdb) is { } series && item.SeasonNumber is { } season =>
                ct => _tmdb.GetSeasonAsync(series, season, ct, extra),
            ItemKind.Episode when PositiveId(item.SeriesProviderIds, ProviderNames.Tmdb) is { } series
                                  && item.SeasonNumber is { } season
                                  && item.EpisodeNumber is { } episode =>
                ct => _tmdb.GetEpisodeAsync(series, season, episode, ct),
            _ => null,
        };
        if (call is null)
        {
            return (Outcome.Skip(item.Kind is ItemKind.Season or ItemKind.Episode
                ? "No TMDB id on the series, or no season/episode number"
                : "No TMDB id"), null);
        }

        try
        {
            return await call(cancellationToken).ConfigureAwait(false) is { } title
                ? (Outcome.Found, title)
                : (Outcome.Skip("TMDB has no such title (404)"), null);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            return (Outcome.Failed(ex.Message), null);
        }
    }

    /// <summary>
    /// TVDB by the item's own TVDB id; else movies and series by their IMDb id, and seasons and
    /// episodes by the series' TVDB id and their numbers (one lookup request more).
    /// </summary>
    private async Task<(Outcome Outcome, TvdbTitle? Title)> FetchTvdbAsync(LibraryItemInfo item, CancellationToken cancellationToken)
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
            return (Outcome.Skip(item.Kind is ItemKind.Season or ItemKind.Episode
                ? "No TVDB id on the item or its series, or no season/episode number"
                : "No TVDB or IMDb id"), null);
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
                ? (Outcome.Skip("TVDB has no such title (404)"), null)
                : (Outcome.Found, title);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            return (Outcome.Failed(ex.Message.StartsWith("TVDB", StringComparison.Ordinal) ? ex.Message : "TVDB: " + ex.Message), null);
        }
    }

    /// <summary>AniList by the item's AniList id (movies and series only).</summary>
    private async Task<(Outcome Outcome, IReadOnlyList<AniListTranslation>? Translations)> FetchAniListAsync(
        LibraryItemInfo item,
        CancellationToken cancellationToken)
    {
        if (AniListId(item) is not { } id)
        {
            // Only anime have one: not worth a note on every other item.
            return (Outcome.Found, null);
        }

        try
        {
            return await _aniList.GetMediaAsync(id, cancellationToken).ConfigureAwait(false) is { } translations
                ? (Outcome.Found, translations)
                : (Outcome.Skip("AniList has no such title (404)"), null);
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            return (Outcome.Failed("AniList: " + ex.Message), null);
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
        string? originalLanguage,
        CancellationToken cancellationToken) =>
        _store.SetFetchStateAsync(
            new FetchState(item.Id, snapshot, _time.GetUtcNow(), status, error, FetchVersion, item.Kind, originalLanguage),
            cancellationToken);

    /// <summary>What one provider gave: a title or nothing to say (found), a note (nothing to fetch), or an error.</summary>
    private sealed record Outcome(string? Note, string? Error)
    {
        public static Outcome Found { get; } = new(null, null);

        public static Outcome Skip(string note) => new(note, null);

        public static Outcome Failed(string error) => new(null, error);
    }
}
