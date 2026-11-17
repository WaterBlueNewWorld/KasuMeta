using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>
/// Prepares the database when the server starts, so problems (an unwritable folder, a
/// downgraded schema, no FTS5) show in the log at once rather than on the first request.
/// </summary>
internal sealed class StoreStartupService : IHostedService
{
    private readonly KasuMetaDatabase _database;
    private readonly ILogger<StoreStartupService> _logger;

    public StoreStartupService(KasuMetaDatabase database, ILogger<StoreStartupService> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A plugin must never stop the server from starting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StoreLog.OpenFailed(_logger, ex, _database.DatabasePath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
