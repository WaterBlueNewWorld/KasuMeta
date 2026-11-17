using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>Source-generated log messages for the store.</summary>
internal static partial class StoreLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "KasuMeta database migrated to schema {Version}")]
    public static partial void Migrated(ILogger logger, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "KasuMeta database ready at {Path} (schema {Version}, FTS5 available: {Fts5})")]
    public static partial void Ready(ILogger logger, string path, int version, bool fts5);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KasuMeta: this server's SQLite has no FTS5; localized search falls back to plain substring matching (no accent folding)")]
    public static partial void NoFts5(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "KasuMeta could not open its database at {Path}")]
    public static partial void OpenFailed(ILogger logger, Exception exception, string path);
}
