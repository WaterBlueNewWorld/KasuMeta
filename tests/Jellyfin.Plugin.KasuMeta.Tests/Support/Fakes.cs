using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;

namespace Jellyfin.Plugin.KasuMeta.Tests.Support;

internal sealed class FakeConfig(PluginConfiguration config) : IKasuMetaConfigProvider
{
    public FakeConfig()
        : this(new PluginConfiguration())
    {
    }

    public PluginConfiguration Current { get; } = config;

    public Version PluginVersion => new(1, 2, 3, 0);
}

/// <summary>A library held in a dictionary; per-user visibility set by hand.</summary>
internal sealed class FakeLibrary : ILibraryItemSource
{
    public Dictionary<Guid, LibraryItemInfo> Items { get; } = [];

    public Dictionary<Guid, HashSet<Guid>> VisibleTo { get; } = [];

    public LibraryItemInfo Add(LibraryItemInfo item)
    {
        Items[item.Id] = item;
        return item;
    }

    public LibraryItemInfo? GetItem(Guid itemId) => Items.GetValueOrDefault(itemId);

    public IReadOnlyList<Guid> ListAllIds() => Items.Keys.ToList();

    public IReadOnlyList<Guid> FilterVisible(Guid userId, IReadOnlyCollection<Guid> itemIds) =>
        itemIds.Where(id => Items.ContainsKey(id) && VisibleTo.TryGetValue(userId, out var set) && set.Contains(id)).ToList();

    public static LibraryItemInfo Movie(Guid id, string? tmdb) =>
        new(id, ItemKind.Movie, Ids(tmdb), Ids(null));

    public static LibraryItemInfo Episode(Guid id, string? seriesTmdb, int? season, int? episode) =>
        new(id, ItemKind.Episode, Ids(null), Ids(seriesTmdb), season, episode);

    private static Dictionary<string, string> Ids(string? tmdb) =>
        tmdb is null ? [] : new Dictionary<string, string> { [ProviderNames.Tmdb] = tmdb };
}

/// <summary>Answers from a script and records the calls.</summary>
internal sealed class FakeTmdb : ITmdbClient
{
    public List<string> Calls { get; } = [];

    public Func<string, IReadOnlyList<TmdbTranslation>?> Answer { get; set; } = _ => [];

    public Task<IReadOnlyList<TmdbTranslation>?> GetMovieTranslationsAsync(int movieId, CancellationToken cancellationToken) =>
        Call($"movie/{movieId}");

    public Task<IReadOnlyList<TmdbTranslation>?> GetSeriesTranslationsAsync(int seriesId, CancellationToken cancellationToken) =>
        Call($"tv/{seriesId}");

    public Task<IReadOnlyList<TmdbTranslation>?> GetSeasonTranslationsAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken) =>
        Call($"tv/{seriesId}/season/{seasonNumber}");

    public Task<IReadOnlyList<TmdbTranslation>?> GetEpisodeTranslationsAsync(
        int seriesId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken) =>
        Call($"tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}");

    private Task<IReadOnlyList<TmdbTranslation>?> Call(string path)
    {
        Calls.Add(path);
        return Task.FromResult(Answer(path));
    }
}
