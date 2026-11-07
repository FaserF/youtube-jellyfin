using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Base class for local metadata providers reading companion .info.json files.
/// </summary>
public abstract class AbstractYouTubeLocalProvider<TItem> : ILocalMetadataProvider<TItem>, IHasItemChangeMonitor, IHasOrder
    where TItem : BaseItem, new()
{
    protected readonly ILogger _logger;

    protected AbstractYouTubeLocalProvider(ILogger logger)
    {
        _logger = logger;
    }

    public virtual string Name => "YouTube (Local .info.json)";
    public virtual int Order => 1;

    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        var infoJson = YTDLInfoJsonHelper.FindInfoJsonPath(item.Path);
        if (infoJson != null && File.Exists(infoJson))
        {
            var jsonWriteTime = File.GetLastWriteTimeUtc(infoJson);
            return jsonWriteTime > item.DateLastSaved;
        }

        return false;
    }

    public Task<MetadataResult<TItem>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<TItem>();
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableLocalMetadataProvider)
        {
            return Task.FromResult(result);
        }

        if (string.IsNullOrWhiteSpace(info.Path))
        {
            return Task.FromResult(result);
        }

        var jsonPath = YTDLInfoJsonHelper.FindInfoJsonPath(info.Path);
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
        {
            return Task.FromResult(result);
        }

        try
        {
            var data = YTDLInfoJsonHelper.ReadInfoJson(jsonPath, cancellationToken);
            if (data != null)
            {
                var item = new TItem();
                PopulateMetadata(item, data, info);
                result.Item = item;
                result.HasMetadata = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube Plugin: Failed to parse .info.json at '{Path}'", jsonPath);
        }

        return Task.FromResult(result);
    }

    protected abstract void PopulateMetadata(TItem item, YTDLData data, ItemInfo info);
}
