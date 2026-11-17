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

    [LoggerMessage(Level = LogLevel.Information, Message = "KasuMeta {Task}: queued {Queued} of {Considered} items")]
    public static partial void TaskQueued(ILogger logger, string task, int queued, int considered);
}
