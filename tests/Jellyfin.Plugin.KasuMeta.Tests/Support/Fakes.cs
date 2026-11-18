using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Providers.AniList;
using Jellyfin.Plugin.KasuMeta.Providers.Tmdb;
using Jellyfin.Plugin.KasuMeta.Providers.Tvdb;

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

    public IReadOnlyList<LibraryItemInfo> GetVisibleItems(Guid? userId, IReadOnlyCollection<Guid> itemIds) =>
        itemIds
            .Where(id => Items.ContainsKey(id) && (userId is null || (VisibleTo.TryGetValue(userId.Value, out var set) && set.Contains(id))))
            .Select(id => Items[id])
            .ToList();

    public static LibraryItemInfo Series(Guid id, string? tmdb) =>
        new(id, ItemKind.Series, Ids(tmdb), Ids(null));

    public static LibraryItemInfo Season(Guid id, string? seriesTmdb, int? season) =>
        new(id, ItemKind.Season, Ids(null), Ids(seriesTmdb), season);

    public static LibraryItemInfo Movie(Guid id, string? tmdb) =>
        new(id, ItemKind.Movie, Ids(tmdb), Ids(null));

    public static LibraryItemInfo Episode(Guid id, string? seriesTmdb, int? season, int? episode) =>
        new(id, ItemKind.Episode, Ids(null), Ids(seriesTmdb), season, episode);

    /// <summary>An item with the given provider ids (e.g. Tmdb and AniList).</summary>
    public static LibraryItemInfo WithIds(Guid id, ItemKind kind, params (string Provider, string Id)[] ids) =>
        new(id, kind, ids.ToDictionary(i => i.Provider, i => i.Id), Ids(null));

    /// <summary>A season or episode with its own ids and its series' ids.</summary>
    public static LibraryItemInfo Child(
        Guid id,
        ItemKind kind,
        (string Provider, string Id)[] own,
        (string Provider, string Id)[] series,
        int? season,
        int? episode = null) =>
        new(id, kind, own.ToDictionary(i => i.Provider, i => i.Id), series.ToDictionary(i => i.Provider, i => i.Id), season, episode);

    private static Dictionary<string, string> Ids(string? tmdb) =>
        tmdb is null ? [] : new Dictionary<string, string> { [ProviderNames.Tmdb] = tmdb };
}

/// <summary>Answers AniList lookups from a script and records the ids asked for.</summary>
internal sealed class FakeAniList : IAniListClient
{
    public List<int> Calls { get; } = [];

    public Func<int, IReadOnlyList<AniListTranslation>?> Answer { get; set; } = _ => [];

    public Task<IReadOnlyList<AniListTranslation>?> GetMediaAsync(int mediaId, CancellationToken cancellationToken)
    {
        Calls.Add(mediaId);
        return Task.FromResult(Answer(mediaId));
    }
}

/// <summary>Answers TVDB lookups from scripts and records the calls (<c>movie/963</c>, <c>find/season/370931/1</c>).</summary>
internal sealed class FakeTvdb : ITvdbClient
{
    public List<string> Calls { get; } = [];

    public Func<string, TvdbTitle?> Answer { get; set; } = _ => new TvdbTitle([], []);

    /// <summary>Gets or sets the answer to the id lookups; the default finds nothing.</summary>
    public Func<string, int?> Find { get; set; } = _ => null;

    public Task<TvdbTitle?> GetMovieAsync(int movieId, CancellationToken cancellationToken) => Call($"movie/{movieId}");

    public Task<TvdbTitle?> GetSeriesAsync(int seriesId, CancellationToken cancellationToken) => Call($"series/{seriesId}");

    public Task<TvdbTitle?> GetSeasonAsync(int seasonId, CancellationToken cancellationToken) => Call($"season/{seasonId}");

    public Task<TvdbTitle?> GetEpisodeAsync(int episodeId, CancellationToken cancellationToken) => Call($"episode/{episodeId}");

    public Task<int?> FindSeasonIdAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken) =>
        Lookup($"find/season/{seriesId}/{seasonNumber}");

    public Task<int?> FindEpisodeIdAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken) =>
        Lookup($"find/episode/{seriesId}/{seasonNumber}/{episodeNumber}");

    public Task<int?> FindByImdbIdAsync(string imdbId, bool series, CancellationToken cancellationToken) =>
        Lookup($"find/{(series ? "series" : "movie")}/{imdbId}");

    private Task<TvdbTitle?> Call(string path)
    {
        Calls.Add(path);
        return Task.FromResult(Answer(path));
    }

    private Task<int?> Lookup(string path)
    {
        Calls.Add(path);
        return Task.FromResult(Find(path));
    }
}

/// <summary>Raises <see cref="Changed"/> by hand, as saving the plugin page would.</summary>
internal sealed class FakeConfigurationChanges : IConfigurationChanges
{
    public event EventHandler<PluginConfiguration>? Changed;

    public void Raise(PluginConfiguration config) => Changed?.Invoke(this, config);
}

/// <summary>Answers from a script and records the calls.</summary>
internal sealed class FakeTmdb : ITmdbClient
{
    public List<string> Calls { get; } = [];

    public Func<string, TmdbTitle?> Answer { get; set; } = _ => new TmdbTitle([], []);

    public Task<TmdbTitle?> GetMovieAsync(int movieId, CancellationToken cancellationToken) =>
        Call($"movie/{movieId}");

    public Task<TmdbTitle?> GetSeriesAsync(int seriesId, CancellationToken cancellationToken) =>
        Call($"tv/{seriesId}");

    public Task<TmdbTitle?> GetSeasonAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken) =>
        Call($"tv/{seriesId}/season/{seasonNumber}");

    public Task<TmdbTitle?> GetEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken) =>
        Call($"tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}");

    private Task<TmdbTitle?> Call(string path)
    {
        Calls.Add(path);
        return Task.FromResult(Answer(path));
    }
}
