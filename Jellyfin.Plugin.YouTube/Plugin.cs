using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.YouTube;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "YouTube";

    public override Guid Id => Guid.Parse("a9f4e2c1-8b7d-4e6a-9c5b-3d2e1f0a8b9c");

    public override string Description =>
        "SmartTube 1:1 Parity YouTube Integration for Jellyfin: Multi-User TV OAuth Login, Personal Home/Subscriptions/History/WatchLater Feeds, 4K Ad-Free Direct Streaming, SponsorBlock Auto-Skip, ReturnYouTubeDislike & Favorite-to-Like Sync.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
