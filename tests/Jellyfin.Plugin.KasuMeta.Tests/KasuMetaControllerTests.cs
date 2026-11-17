using System.Reflection;
using Jellyfin.Plugin.KasuMeta.Api;
using Jellyfin.Plugin.KasuMeta.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KasuMeta.Tests;

public class KasuMetaControllerTests
{
    private sealed class FakeConfig(PluginConfiguration config) : IKasuMetaConfigProvider
    {
        public PluginConfiguration Current => config;

        public Version PluginVersion => new(1, 2, 3, 0);
    }

    [Fact]
    public void Info_reports_version_and_configured_locales()
    {
        var config = new PluginConfiguration
        {
            EnabledLocales = ["es-MX", "ja-JP"],
            FallbackToBaseLanguage = false,
            TmdbApiKey = "secret",
        };
        var controller = new KasuMetaController(new FakeConfig(config));

        var info = controller.GetInfo().Value;

        Assert.NotNull(info);
        Assert.Equal("KasuMeta", info.Name);
        Assert.Equal("1.2.3.0", info.Version);
        Assert.Equal(KasuMetaController.ApiVersion, info.ApiVersion);
        Assert.Equal(["es-MX", "ja-JP"], info.EnabledLocales);
        Assert.False(info.FallbackToBaseLanguage);
    }

    [Fact]
    public void Info_never_exposes_provider_keys()
    {
        var properties = typeof(Api.Models.KasuMetaInfo)
            .GetProperties()
            .Select(p => p.Name);

        Assert.DoesNotContain(properties, n => n.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Controller_requires_jellyfin_auth_and_lives_under_KasuMeta()
    {
        var type = typeof(KasuMetaController);

        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("KasuMeta", type.GetCustomAttribute<RouteAttribute>()?.Template);
    }

    [Fact]
    public void Config_page_is_embedded_where_Plugin_GetPages_points()
    {
        var expected = $"{typeof(Plugin).Namespace}.Configuration.configPage.html";

        Assert.Contains(expected, typeof(Plugin).Assembly.GetManifestResourceNames());
    }
}
