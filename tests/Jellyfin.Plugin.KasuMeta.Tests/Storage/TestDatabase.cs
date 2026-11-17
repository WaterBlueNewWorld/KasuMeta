using Jellyfin.Plugin.KasuMeta.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Storage;

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A fresh database file in a temp folder, deleted afterwards.</summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string _folder;

    /// <param name="fts5">Null: probe the bundled SQLite (it has FTS5). False: pretend it hasn't.</param>
    public TestDatabase(bool? fts5 = null)
    {
        _folder = Path.Combine(Path.GetTempPath(), "kasumeta-tests", Guid.NewGuid().ToString("N"));
        DbPath = Path.Combine(_folder, "nested", "kasumeta.db");
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        Database = Open(fts5);
        Store = new SqliteLocalizedMetadataStore(Database, Time);
    }

    /// <summary>Opens the same file again, as a server restart (or another server) would.</summary>
    public KasuMetaDatabase Open(bool? fts5 = null) =>
        new(DbPath, Time, NullLogger<KasuMetaDatabase>.Instance) { Fts5Override = fts5 };

    public string DbPath { get; }

    public ManualTimeProvider Time { get; }

    public KasuMetaDatabase Database { get; }

    public SqliteLocalizedMetadataStore Store { get; }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await Database.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public void Dispose()
    {
        // Clears this file's pool only. ClearAllPools here made a parallel test fail now and then
        // ("Cannot access a disposed object: SQLitePCL.sqlite3") by closing its connection mid-use.
        Database.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Still held (e.g. a reader the OS hasn't released yet); the temp cleanup gets it.
        }
    }
}
