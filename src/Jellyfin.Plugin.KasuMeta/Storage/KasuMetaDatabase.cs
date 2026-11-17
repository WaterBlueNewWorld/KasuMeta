using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>
/// KasuMeta's own SQLite file. It never touches Jellyfin's database. It opens connections,
/// and on first use it turns on WAL, runs migrations and probes for FTS5.
/// </summary>
/// <remarks>
/// The SQLite native library comes from the server (Jellyfin's EF Core provider has already
/// initialized it), so this class never calls <c>SQLitePCL.Batteries.Init()</c>.
/// </remarks>
public sealed class KasuMetaDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly ILogger<KasuMetaDatabase> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public KasuMetaDatabase(string databasePath, TimeProvider time, ILogger<KasuMetaDatabase> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        _time = time;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Gets the path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets a value indicating whether the server's SQLite has FTS5.</summary>
    public bool SupportsFts5 { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the full-text index over localized names is built. When
    /// false (no FTS5), search still works by substring matching, without accent folding.
    /// </summary>
    public bool SearchIndexReady { get; private set; }

    /// <summary>Gets or sets a stand-in for the FTS5 probe, so tests can run the no-FTS5 path.</summary>
    internal bool? Fts5Override { get; set; }

    /// <summary>Gets the schema version after initialization (0 before it).</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>Opens a connection to an initialized database. The caller disposes it.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the file and brings the schema up to date. Safe to call many times.</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var folder = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                // WAL lets the API read while the fetch worker writes. The mode sticks to the file.
                await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
                SchemaVersion = await Migrations
                    .MigrateAsync(connection, _time.GetUtcNow(), _logger, cancellationToken)
                    .ConfigureAwait(false);
                SupportsFts5 = Fts5Override
                    ?? await ProbeFts5Async(connection, cancellationToken).ConfigureAwait(false);
                SearchIndexReady = await SearchIndex
                    .EnsureAsync(connection, SupportsFts5, cancellationToken)
                    .ConfigureAwait(false);
            }

            _initialized = true;
            StoreLog.Ready(_logger, DatabasePath, SchemaVersion, SupportsFts5);
            if (!SupportsFts5)
            {
                StoreLog.NoFts5(_logger);
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Releases the lock and this database's pooled connections (and so the file). Only this
    /// file's pool: <see cref="SqliteConnection.ClearAllPools"/> would also tear connections out
    /// from under every other SQLite user in the process.
    /// </summary>
    public void Dispose()
    {
        _initLock.Dispose();
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>Checks that this SQLite build can create an FTS5 table, using a throwaway temp table.</summary>
    internal static async Task<bool> ProbeFts5Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(
                    connection,
                    "CREATE VIRTUAL TABLE temp.kasumeta_fts5_probe USING fts5(x); DROP TABLE temp.kasumeta_fts5_probe;",
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
