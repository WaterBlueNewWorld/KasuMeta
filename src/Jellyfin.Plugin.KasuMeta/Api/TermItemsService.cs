using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// Finds the items that carry a genre or a tag, by the name an overlay shows for it. A client
/// draws an item's genres and tags as the overlay serves them ("Terror" for a Spanish user,
/// where Jellyfin's own genre is "Horror"), and Jellyfin's item queries only know its own names:
/// this is how such a name leads back to every item that shows it.
/// </summary>
/// <remarks>
/// An item matches when the list it is served in the caller's locale holds the term, resolved
/// exactly as <see cref="OverlayService"/> does (the locale's stored list with the dictionary's
/// fill, else the English terms translated by the dictionary, else the original language's), so
/// what a page shows and what this finds can't drift apart. Names compare without case or
/// accents. Every item's rows of the field are read per request: one small query, no index to
/// keep in step with the dictionary.
/// </remarks>
public sealed class TermItemsService
{
    private readonly ILocalizedMetadataStore _store;
    private readonly ILibraryItemSource _library;
    private readonly IKasuMetaConfigProvider _config;
    private readonly TermDictionary _terms;

    public TermItemsService(
        ILocalizedMetadataStore store,
        ILibraryItemSource library,
        IKasuMetaConfigProvider config,
        TermDictionary terms)
    {
        _store = store;
        _library = library;
        _config = config;
        _terms = terms;
    }

    /// <summary>Finds the items served <paramref name="term"/> among their genres or tags.</summary>
    /// <param name="kind">One of <see cref="TermKinds"/>.</param>
    /// <param name="term">The genre or tag as shown (not empty).</param>
    /// <param name="locale">The caller's normalized locale.</param>
    /// <param name="kinds">Item kinds wanted.</param>
    /// <param name="startIndex">How many matches to skip.</param>
    /// <param name="limit">Most results returned.</param>
    /// <param name="userId">The caller, or null for an API key (sees everything).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<TermItemsResponse> FindAsync(
        string kind,
        string term,
        string locale,
        IReadOnlySet<ItemKind> kinds,
        int startIndex,
        int limit,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(kinds);
        var field = TermKinds.FieldOf(kind);
        var config = _config.Current;
        var rows = await _store.GetListRowsAsync(field, cancellationToken).ConfigureAwait(false);
        var byItem = rows.ToLookup(r => r.ItemId);
        var originals = config.FallbackToOriginalLanguage
            ? await _store.GetOriginalLanguagesAsync(byItem.Select(g => g.Key).ToList(), cancellationToken).ConfigureAwait(false)
            : new Dictionary<Guid, string>();
        var options = new OverlayService.ResolveOptions(
            config.FallbackToBaseLanguage,
            config.EnabledLocales,
            await _terms.GetAsync(cancellationToken).ConfigureAwait(false));

        var wanted = SearchQuery.Fold(term);
        var matches = new List<Guid>();
        foreach (var item in byItem)
        {
            var served = OverlayService.ResolveListValues(
                field,
                item.ToList(),
                locale,
                options with { OriginalLanguage = originals.GetValueOrDefault(item.Key) });
            if (served is not null && served.Values.Any(value => SearchQuery.Fold(value) == wanted))
            {
                matches.Add(item.Key);
            }
        }

        // The whole match set is filtered, so Total is exact and a hidden item never counts.
        var visible = _library
            .GetVisibleItems(userId, matches)
            .Where(item => kinds.Contains(item.Kind))
            .OrderBy(item => item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToList();

        return new TermItemsResponse
        {
            Kind = kind,
            Term = term,
            Locale = locale,
            Total = visible.Count,
            StartIndex = startIndex,
            Results = visible
                .Skip(startIndex)
                .Take(limit)
                .Select(item => new TermItem(item.Id.ToString("N", CultureInfo.InvariantCulture), item.Kind.ToString()))
                .ToList(),
        };
    }
}
