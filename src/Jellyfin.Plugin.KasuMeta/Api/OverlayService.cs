using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// Builds localized overlays from the store. Each field and each image type resolves its locale
/// on its own (exact → base language → sibling), because coverage differs: TMDB may have a
/// Mexican title but only a Spanish (Spain) overview, or a Japanese logo but no Japanese poster.
/// </summary>
public sealed class OverlayService
{
    private readonly ILocalizedMetadataStore _store;
    private readonly IKasuMetaConfigProvider _config;

    public OverlayService(ILocalizedMetadataStore store, IKasuMetaConfigProvider config)
    {
        _store = store;
        _config = config;
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
        var fieldsByItem = rows.Where(r => fields.Contains(r.Field)).ToLookup(r => r.ItemId);
        var imagesByItem = images.Where(i => imageTypes.Contains(i.ImageType)).ToLookup(i => i.ItemId);

        var items = new Dictionary<string, ItemOverlay>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in requestedIds.Distinct())
        {
            var key = id.ToString("N", CultureInfo.InvariantCulture);
            var overlay = visibleIds.Contains(id)
                ? Resolve(fieldsByItem[id], imagesByItem[id], locale, config.FallbackToBaseLanguage, config.EnabledLocales)
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
        foreach (var locale in fieldsByLocale.Select(g => g.Key).Union(imagesByLocale.Select(g => g.Key), StringComparer.Ordinal))
        {
            // Exact match only: each locale shows what is stored under it.
            if (Resolve(fieldsByLocale[locale], imagesByLocale[locale], locale, false, config.EnabledLocales) is { } overlay)
            {
                locales[locale] = overlay;
            }
        }

        return new ItemTranslations
        {
            ItemId = itemId.ToString("N", CultureInfo.InvariantCulture),
            Locales = locales,
            Fetch = state is null ? null : new ItemFetchInfo(FormatStatus(state.Status), state.LastFetch, state.LastError),
        };
    }

    private static string FormatStatus(FetchStatus status) => status switch
    {
        FetchStatus.Pending => "pending",
        FetchStatus.Ok => "ok",
        FetchStatus.Error => "error",
        FetchStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>Resolves one item's rows into an overlay, or null if nothing resolves.</summary>
    internal static ItemOverlay? Resolve(
        IEnumerable<LocalizedField> rows,
        IEnumerable<LocalizedImage> images,
        string locale,
        bool fallbackToBase,
        IReadOnlyList<string> enabledLocales)
    {
        var overlay = new ItemOverlay();
        var any = false;

        foreach (var field in rows.GroupBy(r => r.Field, StringComparer.Ordinal))
        {
            if (Pick(field, r => r.Locale) is not { } row)
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
            if (Pick(type, i => i.Locale) is not { } image)
            {
                continue;
            }

            overlay.Images ??= new Dictionary<string, LocalizedImageValue>(StringComparer.Ordinal);
            overlay.Images[type.Key] = new LocalizedImageValue(
                image.Url, image.Width, image.Height, image.Source, image.Locale, image.Locked);
            Seen(image.UpdatedAt);
        }

        return any ? overlay : null;

        T? Pick<T>(IEnumerable<T> candidates, Func<T, string> localeOf)
            where T : class
        {
            var byLocale = candidates.ToDictionary(localeOf, StringComparer.Ordinal);
            var resolved = LocaleChain.Resolve(locale, byLocale.Keys, fallbackToBase, enabledLocales);
            return resolved is not null && byLocale.TryGetValue(resolved, out var picked) ? picked : null;
        }

        void Seen(DateTimeOffset updatedAt)
        {
            any = true;
            if (updatedAt > overlay.UpdatedAt)
            {
                overlay.UpdatedAt = updatedAt;
            }
        }
    }
}
