using Jellyfin.Plugin.KasuMeta.Configuration;
using Jellyfin.Plugin.KasuMeta.Fetching;
using Jellyfin.Plugin.KasuMeta.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Fetching;

public sealed class ProviderChangeServiceTests
{
    private readonly FakeLibrary _library = new();
    private readonly FetchQueue _queue = new();
    private readonly FakeConfig _config = new(Keyed(new PluginConfiguration()));
    private readonly FakeConfigurationChanges _changes = new();
    private readonly ProviderChangeService _service;

    public ProviderChangeServiceTests()
    {
        _library.Add(FakeLibrary.Movie(Guid.NewGuid(), "1"));
        _library.Add(FakeLibrary.Series(Guid.NewGuid(), "2"));
        _service = new ProviderChangeService(_changes, _config, _library, _queue, NullLogger<ProviderChangeService>.Instance);
    }

    [Fact]
    public async Task Saving_without_changing_the_providers_queues_nothing()
    {
        await _service.StartAsync(CancellationToken.None);

        Assert.Equal(0, _service.Apply(Keyed(new PluginConfiguration { TmdbWeight = 70, RefreshIntervalHours = 12 })));
        Assert.Equal(0, _queue.Pending);
    }

    [Fact]
    public async Task Turning_tvdb_on_queues_the_library_once()
    {
        await _service.StartAsync(CancellationToken.None);
        var config = Keyed(new PluginConfiguration { TvdbEnabled = true });

        Assert.Equal(2, _service.Apply(config));
        Assert.Equal(0, _service.Apply(config)); // the same choice saved again
    }

    [Fact]
    public async Task Swapping_which_provider_wins_queues_the_library_but_other_weights_do_not()
    {
        _config.Current.TvdbEnabled = true; // tmdb>tvdb at start
        await _service.StartAsync(CancellationToken.None);

        Assert.Equal(0, _service.Apply(Keyed(new PluginConfiguration { TvdbEnabled = true, TmdbWeight = 60, TvdbWeight = 40 })));
        Assert.Equal(2, _service.Apply(Keyed(new PluginConfiguration { TvdbEnabled = true, TmdbWeight = 40, TvdbWeight = 60 })));
    }

    [Fact]
    public async Task Changing_one_category_queues_the_library()
    {
        await _service.StartAsync(CancellationToken.None);
        var config = Keyed(new PluginConfiguration
        {
            ProviderRules = [new ProviderRule { Category = MetadataCategories.Overview, Providers = ["tvdb", "tmdb"] }],
        });

        Assert.Equal(2, _service.Apply(config));
    }

    [Fact]
    public async Task Setting_the_key_of_a_listed_provider_queues_the_library()
    {
        _config.Current.TvdbApiKey = string.Empty;
        _config.Current.TvdbEnabled = true;
        await _service.StartAsync(CancellationToken.None);

        var withKey = Keyed(new PluginConfiguration { TvdbEnabled = true });
        Assert.Equal(2, _service.Apply(withKey));
    }

    [Fact]
    public async Task The_saved_event_is_followed_and_released()
    {
        await _service.StartAsync(CancellationToken.None);
        _changes.Raise(Keyed(new PluginConfiguration { TmdbEnabled = false, TvdbEnabled = true }));

        // The handler queues off the saving thread.
        for (var i = 0; i < 100 && _queue.Pending == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, _queue.Pending);
        await _service.StopAsync(CancellationToken.None);
    }

    private static PluginConfiguration Keyed(PluginConfiguration config)
    {
        config.TmdbApiKey = "tmdb-key";
        config.TvdbApiKey = "tvdb-key";
        return config;
    }
}
