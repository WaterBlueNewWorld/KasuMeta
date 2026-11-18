using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// Builds localized overlays from the store. Each field and each image type resolves its locale
/// on its own, because coverage differs: TMDB may have a Mexican title but only a Spanish (Spain)
/// overview, or a Japanese logo but no Japanese poster. The order per field:
/// <list type="number">
/// <item>the requested locale (exact, then base language, then a sibling; see <see cref="LocaleChain.Resolve"/>);</item>
/// <item>for genres and tags, the providers' English terms translated by the admin's dictionary;</item>
/// <item>the item's original language, when <see cref="PluginConfiguration.FallbackToOriginalLanguage"/> is on;</item>
/// <item>nothing: the field is left out.</item>
/// </list>
/// </summary>
public sealed class OverlayService
{
    private readonly ILocalizedMetadataStore _store;
    private readonly IKasuMetaConfigProvider _config;
    private readonly TermDictionary _terms;

    public OverlayService(ILocalizedMetadataStore store, IKasuMetaConfigProvider config, TermDictionary terms)
    {
        _store = store;
        _config = config;
        _terms = terms;
    }

    /// <summary>Builds the batch response for ids the caller may already see.</summary>
    /// <param name="requestedIds">Every id the caller asked for, in order.</param>
    /// <param name="visibleIds">The subset the caller may see; the rest are reported missing.</param>
    /// <param name="locale">The normalized requested locale.</param>
    /// <param name="fields">The fields wanted (values of <see cref="MetadataFields"/>).</param>
    /// <param name="imageTypes">The image types wanted (values of <see cref="LocalizedImageTypes"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<BatchResponse> BuildAsync(
        IReadOnlyList<Guid> requestedIds,
        IReadOnlyCollection<Guid> visibleIds,
        string locale,
        IReadOnlySet<string> fields,
        IReadOnlySet<string> imageTypes,
        CancellationToken cancellationToken)
    {
        var config = _config.Current;
        var rows = fields.Count == 0
            ? []
            : await _store.GetFieldsAsync(visibleIds, null, cancellationToken).ConfigureAwait(false);
        var images = imageTypes.Count == 0
            ? []
            : await _store.GetImagesAsync(visibleIds, null, cancellationToken).ConfigureAwait(false);
        var originals = config.FallbackToOriginalLanguage && (fields.Count > 0 || imageTypes.Count > 0)
            ? await _store.GetOriginalLanguagesAsync(visibleIds, cancellationToken).ConfigureAwait(false)
            : new Dictionary<Guid, string>();
        var terms = fields.Overlaps(MetadataFields.ListFields)
            ? await _terms.GetAsync(cancellationToken).ConfigureAwait(false)
            : TermSnapshot.Empty;
        var fieldsByItem = rows.Where(r => fields.Contains(r.Field)).ToLookup(r => r.ItemId);
        var imagesByItem = images.Where(i => imageTypes.Contains(i.ImageType)).ToLookup(i => i.ItemId);
        var options = new ResolveOptions(config.FallbackToBaseLanguage, config.EnabledLocales, terms);

        var items = new Dictionary<string, ItemOverlay>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in requestedIds.Distinct())
        {
            var key = id.ToString("N", CultureInfo.InvariantCulture);
            var overlay = visibleIds.Contains(id)
                ? Resolve(fieldsByItem[id], imagesByItem[id], locale, options with { OriginalLanguage = originals.GetValueOrDefault(id) })
                : null;
            if (overlay is null)
            {
                missing.Add(key);
            }
            else
            {
                items[key] = overlay;
            }
        }

        return new BatchResponse { Locale = locale, Items = items, Missing = missing };
    }

    /// <summary>
    /// Builds the admin view of one item: every stored locale as it is, with no fallback, plus
    /// the item's fetch state.
    /// </summary>
    public async Task<ItemTranslations> BuildAllLocalesAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var config = _config.Current;
        var rows = await _store.GetFieldsAsync([itemId], null, cancellationToken).ConfigureAwait(false);
        var images = await _store.GetImagesAsync([itemId], null, cancellationToken).ConfigureAwait(false);
        var state = await _store.GetFetchStateAsync(itemId, cancellationToken).ConfigureAwait(false);

        var fieldsByLocale = rows.ToLookup(r => r.Locale, StringComparer.Ordinal);
        var imagesByLocale = images.ToLookup(i => i.Locale, StringComparer.Ordinal);
        var locales = new SortedDictionary<string, ItemOverlay>(StringComparer.Ordinal);
        var exact = new ResolveOptions(false, config.EnabledLocales, null);
        foreach (var locale in fieldsByLocale.Select(g => g.Key).Union(imagesByLocale.Select(g => g.Key), StringComparer.Ordinal))
        {
            // Exact match only: each locale shows what is stored under it.
            if (Resolve(fieldsByLocale[locale], imagesByLocale[locale], locale, exact) is { } overlay)
            {
                locales[locale] = overlay;
            }
        }

        return new ItemTranslations
        {
            ItemId = itemId.ToString("N", CultureInfo.InvariantCulture),
            Locales = locales,
            Fetch = state is null ? null : new ItemFetchInfo(FormatStatus(state.Status), state.LastFetch, state.LastError),
            OriginalLanguage = state?.OriginalLanguage,
        };
    }

    /// <summary>The path an uploaded image is served at, relative to the server's base URL.</summary>
    internal static string UploadPath(Guid itemId, string locale, string imageType, DateTimeOffset updatedAt) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"KasuMeta/Items/{itemId:N}/{Uri.EscapeDataString(locale)}/Images/{imageType}?v={updatedAt.ToUnixTimeSeconds()}");

    /// <summary>Resolves one item's rows into an overlay, or null if nothing resolves.</summary>
    internal static ItemOverlay? Resolve(
        IEnumerable<LocalizedField> rows,
        IEnumerable<LocalizedImage> images,
        string locale,
        bool fallbackToBase,
        IReadOnlyList<string> enabledLocales) =>
        Resolve(rows, images, locale, new ResolveOptions(fallbackToBase, enabledLocales, null));

    /// <summary>Resolves one item's rows into an overlay, or null if nothing resolves.</summary>
    internal static ItemOverlay? Resolve(
        IEnumerable<LocalizedField> rows,
        IEnumerable<LocalizedImage> images,
        string locale,
        ResolveOptions options)
    {
        var overlay = new ItemOverlay();
        var any = false;

        foreach (var field in rows.GroupBy(r => r.Field, StringComparer.Ordinal))
        {
            if (MetadataFields.ListFields.Contains(field.Key))
            {
                if (ResolveList(field.Key, field.ToList(), locale, options) is not { } list)
                {
                    continue;
                }

                if (field.Key == MetadataFields.Genres)
                {
                    overlay.Genres = list.Value;
                }
                else
                {
                    overlay.Tags = list.Value;
                }

                Seen(list.UpdatedAt);
                continue;
            }

            if (Pick(field.ToList(), r => r.Locale, locale, options) is not { } row)
            {
                continue;
            }

            var value = new LocalizedValue(row.Value, row.Source, row.Locale, row.Locked);
            switch (field.Key)
            {
                case MetadataFields.Name: overlay.Name = value; break;
                case MetadataFields.Overview: overlay.Overview = value; break;
                case MetadataFields.Tagline: overlay.Tagline = value; break;
                case MetadataFields.SortName: overlay.SortName = value; break;
                default: continue;
            }

            Seen(row.UpdatedAt);
        }

        foreach (var type in images.GroupBy(i => i.ImageType, StringComparer.Ordinal))
        {
            if (Pick(type.ToList(), i => i.Locale, locale, options) is not { } image)
            {
                continue;
            }

            overlay.Images ??= new Dictionary<string, LocalizedImageValue>(StringComparer.Ordinal);
            overlay.Images[type.Key] = new LocalizedImageValue(
                image.HasFile ? UploadPath(image.ItemId, image.Locale, image.ImageType, image.UpdatedAt) : image.Url,
                image.Width,
                image.Height,
                image.Source,
                image.Locale,
                image.Locked,
                image.HasFile);
            Seen(image.UpdatedAt);
        }

        return any ? overlay : null;

        void Seen(DateTimeOffset updatedAt)
        {
            any = true;
            if (updatedAt > overlay.UpdatedAt)
            {
                overlay.UpdatedAt = updatedAt;
            }
        }
    }

    private static string FormatStatus(FetchStatus status) => status switch
    {
        FetchStatus.Pending => "pending",
        FetchStatus.Ok => "ok",
        FetchStatus.Error => "error",
        FetchStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>
    /// The requested locale's row (exact, base, sibling); else, with an original language set,
    /// that language's row, whatever <see cref="ResolveOptions.FallbackToBase"/> says.
    /// </summary>
    private static T? Pick<T>(IReadOnlyList<T> candidates, Func<T, string> localeOf, string locale, ResolveOptions options)
        where T : class
    {
        var byLocale = candidates.ToDictionary(localeOf, StringComparer.Ordinal);
        var resolved = LocaleChain.Resolve(locale, byLocale.Keys, options.FallbackToBase, options.EnabledLocales);
        return resolved is not null && byLocale.TryGetValue(resolved, out var picked) ? picked : PickOriginal(byLocale, options);
    }

    private static T? PickOriginal<T>(Dictionary<string, T> byLocale, ResolveOptions options)
        where T : class
    {
        if (options.OriginalLanguage is not { } original)
        {
            return null;
        }

        var resolved = LocaleChain.Resolve(original, byLocale.Keys, fallbackToBase: true, options.EnabledLocales);
        return resolved is not null && byLocale.TryGetValue(resolved, out var picked) ? picked : null;
    }

    /// <summary>A genres or tags list: stored in the requested locale, else translated by the dictionary, else the original language's.</summary>
    private static (LocalizedList Value, DateTimeOffset UpdatedAt)? ResolveList(
        string field,
        IReadOnlyList<LocalizedField> candidates,
        string locale,
        ResolveOptions options)
    {
        var byLocale = candidates.ToDictionary(r => r.Locale, StringComparer.Ordinal);
        var kind = field == MetadataFields.Genres ? TermKinds.Genre : TermKinds.Tag;
        var resolved = LocaleChain.Resolve(locale, byLocale.Keys, options.FallbackToBase, options.EnabledLocales);
        if (resolved is not null && AsList(byLocale[resolved]) is { } stored)
        {
            // A provider's list may keep English names it has no translation for (TMDB sends
            // "Sci-Fi & Fantasy" in Spanish): the dictionary fills those. An admin's list is kept as is.
            if (stored.Item1.Source != MetadataSources.Manual && Translate(stored.Item1.Values) is { } filled)
            {
                return (stored.Item1 with { Values = filled, Source = MetadataSources.Dictionary }, stored.Item2);
            }

            return stored;
        }

        if (LocaleChain.Resolve(MetadataFetcher.BaseTermsLocale, byLocale.Keys, fallbackToBase: true, options.EnabledLocales) is { } baseLocale
            && Translate(ListValues.Parse(byLocale[baseLocale].Value)) is { } translated)
        {
            return (new LocalizedList(translated, MetadataSources.Dictionary, locale, false), byLocale[baseLocale].UpdatedAt);
        }

        return PickOriginal(byLocale, options) is { } original && AsList(original) is { } list ? list : null;

        // The terms with the dictionary's translations in, or null when it has none of them.
        IReadOnlyList<string>? Translate(IReadOnlyList<string> terms)
        {
            if (options.Terms is not { } dictionary)
            {
                return null;
            }

            var hits = 0;
            var values = terms
                .Select(term =>
                {
                    if (dictionary.Translate(kind, term, locale, options.FallbackToBase, options.EnabledLocales) is { } hit)
                    {
                        hits++;
                        return hit.Value;
                    }

                    return term;
                })
                .ToList();
            return hits > 0 ? ListValues.Clean(values) : null;
        }

        static (LocalizedList, DateTimeOffset)? AsList(LocalizedField row)
        {
            var values = ListValues.Parse(row.Value);
            return values.Count == 0 ? null : (new LocalizedList(values, row.Source, row.Locale, row.Locked), row.UpdatedAt);
        }
    }

    /// <summary>How an overlay resolves.</summary>
    /// <param name="FallbackToBase">The plugin's <c>FallbackToBaseLanguage</c> setting.</param>
    /// <param name="EnabledLocales">Preferred order among siblings.</param>
    /// <param name="Terms">The genre and tag dictionary, or null for none.</param>
    /// <param name="OriginalLanguage">The item's original language when that fallback is on, else null.</param>
    internal sealed record ResolveOptions(
        bool FallbackToBase,
        IReadOnlyList<string> EnabledLocales,
        TermSnapshot? Terms,
        string? OriginalLanguage = null);
}
