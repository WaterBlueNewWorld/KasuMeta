using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Fetching;

/// <summary>Source-generated log messages for fetching and the scheduled tasks.</summary>
internal static partial class FetchLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "KasuMeta fetch {ItemId}: {Result} ({Pending} waiting)")]
    public static partial void Fetched(ILogger logger, Guid itemId, FetchResult result, int pending);

    [LoggerMessage(Level = LogLevel.Error, Message = "KasuMeta fetch {ItemId} crashed")]
    public static partial void Crashed(ILogger logger, Exception exception, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KasuMeta could not remove the rows of deleted item {ItemId}")]
    public static partial void DeleteFailed(ILogger logger, Exception exception, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KasuMeta could not fetch {ItemName} ({ItemId}): {Error}")]
    public static partial void ProviderFailed(ILogger logger, string? itemName, Guid itemId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "KasuMeta fetch queue empty: {Fetched} fetched, {Unchanged} unchanged, {Skipped} skipped, {Failed} failed, {Removed} removed")]
    public static partial void Drained(ILogger logger, int fetched, int unchanged, int skipped, int failed, int removed);

    [LoggerMessage(Level = LogLevel.Information, Message = "KasuMeta {Task}: queued {Queued} of {Considered} items")]
    public static partial void TaskQueued(ILogger logger, string task, int queued, int considered);
}
