using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Api.Models;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Localization;
using Jellyfin.Plugin.KasuMeta.Search;
using Jellyfin.Plugin.KasuMeta.Storage;

namespace Jellyfin.Plugin.KasuMeta.Api;

/// <summary>
/// Searches localized names. Every stored locale is searched, so a title can be found by its
/// name in any enabled language (a Spanish user typing the Japanese title still finds it);
/// names in the caller's locale rank first and are the ones reported.
/// </summary>
public sealed class SearchService
{
    /// <summary>
    /// Most name rows read from the store per search. Ranking, de-duplication and the visibility
    /// filter work on this set, so a very broad query (one letter) may miss weaker matches.
    /// </summary>
    internal const int MaxCandidates = 500;

    private const int VisibilityChunk = 50;

    private readonly ILocalizedMetadataStore _store;
    private readonly ILibraryItemSource _library;

    public SearchService(ILocalizedMetadataStore store, ILibraryItemSource library)
    {
        _store = store;
        _library = library;
    }

    /// <summary>Runs a search.</summary>
    /// <param name="query">The parsed query (not empty).</param>
    /// <param name="locale">The caller's normalized locale; its names rank first.</param>
    /// <param name="limit">Most results returned.</param>
    /// <param name="kinds">Item kinds wanted.</param>
    /// <param name="userId">The caller, or null for an API key (sees everything).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<SearchResponse> SearchAsync(
        SearchQuery query,
        string locale,
        int limit,
        IReadOnlySet<ItemKind> kinds,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hits = await _store.SearchNamesAsync(query, kinds, MaxCandidates, cancellationToken).ConfigureAwait(false);
        var results = new List<SearchResult>(Math.Min(limit, hits.Count));

        // Visibility is checked lazily, best matches first, and only until the page is full:
        // a restricted user costs a few more lookups, never a leaked title.
        foreach (var chunk in Rank(hits, query.Text, locale).Chunk(VisibilityChunk))
        {
            var visible = _library
                .GetVisibleItems(userId, chunk.Select(h => h.ItemId).ToList())
                .ToDictionary(i => i.Id);
            foreach (var hit in chunk)
            {
                if (!visible.TryGetValue(hit.ItemId, out var item) || !kinds.Contains(item.Kind))
                {
                    continue;
                }

                results.Add(new SearchResult(
                    hit.ItemId.ToString("N", CultureInfo.InvariantCulture),
                    item.Kind.ToString(),
                    hit.Name,
                    hit.Locale));
                if (results.Count == limit)
                {
                    return Response();
                }
            }
        }

        return Response();

        SearchResponse Response() => new() { Query = query.Text, Locale = locale, Results = results };
    }

    /// <summary>
    /// Orders hits and keeps one per item. The name reported for an item is its match in the
    /// caller's locale if there is one, else in the same language, else any. Items are ordered
    /// by how well any of their names matches (exact, then starts-with, then contains), then
    /// movies and series before seasons before episodes (an episode named after its series
    /// shouldn't outrank it), then by that locale preference, then by the store's relevance.
    /// </summary>
    private static int KindRank(ItemKind? kind) => kind switch
    {
        ItemKind.Movie or ItemKind.Series => 0,
        ItemKind.Season => 1,
        ItemKind.Episode => 2,
        _ => 1, // not recorded yet: neither favoured nor buried
    };

    internal static IReadOnlyList<NameHit> Rank(IReadOnlyList<NameHit> hits, string queryText, string locale)
    {
        var folded = SearchQuery.Fold(queryText);
        var group = LocaleChain.FallbackGroup(locale);

        int LocaleRank(string hitLocale) =>
            hitLocale == locale ? 0 : LocaleChain.FallbackGroup(hitLocale) == group ? 1 : 2;

        int NameRank(string name)
        {
            var foldedName = SearchQuery.Fold(name);
            return foldedName == folded ? 0 : foldedName.StartsWith(folded, StringComparison.Ordinal) ? 1 : 2;
        }

        return hits
            .Select((hit, order) => (Hit: hit, Order: order, Locale: LocaleRank(hit.Locale), Name: NameRank(hit.Name)))
            .GroupBy(x => x.Hit.ItemId)
            .Select(item =>
            {
                var shown = item.OrderBy(x => x.Locale).ThenBy(x => x.Name).ThenBy(x => x.Order).First();
                return (shown.Hit, BestName: item.Min(x => x.Name), shown.Locale, Order: item.Min(x => x.Order));
            })
            .OrderBy(x => x.BestName)
            .ThenBy(x => KindRank(x.Hit.Kind))
            .ThenBy(x => x.Locale)
            .ThenBy(x => x.Order)
            .Select(x => x.Hit)
            .ToList();
    }
}
