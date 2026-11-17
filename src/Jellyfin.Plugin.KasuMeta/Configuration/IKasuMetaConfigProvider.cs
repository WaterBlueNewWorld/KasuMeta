namespace Jellyfin.Plugin.KasuMeta.Configuration;

/// <summary>
/// Reads the current plugin configuration. Lets services and controllers avoid the static
/// <see cref="Plugin.Instance"/>, so tests can hand them a plain configuration.
/// </summary>
public interface IKasuMetaConfigProvider
{
    /// <summary>Gets the configuration as it is now (it changes when an admin saves the page).</summary>
    PluginConfiguration Current { get; }

    /// <summary>Gets the plugin's version.</summary>
    Version PluginVersion { get; }
}

/// <summary>The production provider, backed by the loaded plugin instance.</summary>
internal sealed class PluginConfigProvider : IKasuMetaConfigProvider
{
    public PluginConfiguration Current =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("KasuMeta is not loaded.");

    public Version PluginVersion =>
        Plugin.Instance?.Version ?? typeof(Plugin).Assembly.GetName().Version ?? new Version(0, 0);
}
