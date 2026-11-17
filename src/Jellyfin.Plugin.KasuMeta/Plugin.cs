using System.Globalization;
using Jellyfin.Plugin.KasuMeta.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.KasuMeta;

/// <summary>
/// KasuMeta: localized metadata (titles, overviews, artwork) for the client app,
/// stored beside Jellyfin rather than inside it.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Stable plugin id. Never change it: Jellyfin keys installs and config by it.</summary>
    public static readonly Guid PluginId = Guid.Parse("703b8e8e-7d16-40bd-8f92-87fad8b741ae");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running instance (set by Jellyfin when it loads the plugin).</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "KasuMeta";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Localized titles, overviews and artwork for the client app.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
        ];
    }
}
