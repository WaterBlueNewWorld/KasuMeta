using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// Builds localized overlays from the store. Each field resolves its locale on its own (exact →
/// base language → sibling), because coverage differs by field: TMDB may have a Mexican title
/// but only a Spanish (Spain) overview.
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
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<BatchResponse> BuildAsync(
        IReadOnlyList<Guid> requestedIds,
        IReadOnlyCollection<Guid> visibleIds,
        string locale,
        IReadOnlySet<string> fields,
        CancellationToken cancellationToken)
    {
        var rows = await _store.GetFieldsAsync(visibleIds, null, cancellationToken).ConfigureAwait(false);
        var config = _config.Current;
        var byItem = rows.Where(r => fields.Contains(r.Field)).ToLookup(r => r.ItemId);

        var items = new Dictionary<string, ItemOverlay>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in requestedIds.Distinct())
        {
            var key = id.ToString("N", CultureInfo.InvariantCulture);
            var overlay = visibleIds.Contains(id)
                ? Resolve(byItem[id], locale, config.FallbackToBaseLanguage, config.EnabledLocales)
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

    /// <summary>Resolves one item's rows into an overlay, or null if no field resolves.</summary>
    internal static ItemOverlay? Resolve(
        IEnumerable<LocalizedField> rows,
        string locale,
        bool fallbackToBase,
        IReadOnlyList<string> enabledLocales)
    {
        var overlay = new ItemOverlay();
        var any = false;
        foreach (var field in rows.GroupBy(r => r.Field, StringComparer.Ordinal))
        {
            var byLocale = field.ToDictionary(r => r.Locale, StringComparer.Ordinal);
            var resolved = LocaleChain.Resolve(locale, byLocale.Keys, fallbackToBase, enabledLocales);
            if (resolved is null || !byLocale.TryGetValue(resolved, out var row))
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

            any = true;
            if (row.UpdatedAt > overlay.UpdatedAt)
            {
                overlay.UpdatedAt = row.UpdatedAt;
            }
        }

        return any ? overlay : null;
    }
}
