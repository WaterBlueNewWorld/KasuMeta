using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KasuMeta.Configuration;

/// <summary>Tells when an admin saves the plugin's configuration (a seam over the plugin instance).</summary>
public interface IConfigurationChanges
{
    /// <summary>Raised after the configuration was saved, with the new configuration.</summary>
    event EventHandler<PluginConfiguration>? Changed;
}

/// <summary>
/// The production source: Jellyfin's <c>BasePlugin.ConfigurationChanged</c>, which is a
/// property holding a delegate rather than an event, so it's combined, never assigned.
/// </summary>
internal sealed class PluginConfigurationChanges : IConfigurationChanges, IDisposable
{
    private readonly Plugin? _plugin;

    public PluginConfigurationChanges()
    {
        _plugin = Plugin.Instance;
        if (_plugin is not null)
        {
            _plugin.ConfigurationChanged += OnChanged;
        }
    }

    /// <inheritdoc />
    public event EventHandler<PluginConfiguration>? Changed;

    public void Dispose()
    {
        if (_plugin is not null)
        {
            _plugin.ConfigurationChanged -= OnChanged;
        }
    }

    private void OnChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            Changed?.Invoke(this, config);
        }
    }
}
