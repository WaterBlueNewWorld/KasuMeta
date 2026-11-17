using Jellyfin.Plugin.KasuMeta.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.KasuMeta.Tests.Storage;

public class KasuMetaDatabaseTests
{
    [Fact]
    public async Task First_use_creates_the_folder_the_schema_and_WAL()
    {
        using var db = new TestDatabase();

        await db.Database.EnsureInitializedAsync(CancellationToken.None);

        Assert.True(File.Exists(db.DbPath));
        Assert.Equal(Migrations.Latest, db.Database.SchemaVersion);
        Assert.Equal("wal", await db.ScalarAsync("PRAGMA journal_mode;"));
        foreach (var table in new[] { "localized_field", "localized_image", "fetch_state", "schema_version" })
        {
            Assert.Equal(1L, await db.ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));
        }
    }

    [Fact]
    public async Task Reopening_does_not_reapply_migrations()
    {
        using var db = new TestDatabase();
        await db.Database.EnsureInitializedAsync(CancellationToken.None);

        using var again = new KasuMetaDatabase(db.DbPath, db.Time, NullLogger<KasuMetaDatabase>.Instance);
        await again.EnsureInitializedAsync(CancellationToken.None);

        Assert.Equal((long)Migrations.All.Count, await db.ScalarAsync("SELECT COUNT(*) FROM schema_version;"));
    }

    [Fact]
    public async Task A_schema_newer_than_the_plugin_is_refused()
    {
        using var db = new TestDatabase();
        await db.Database.EnsureInitializedAsync(CancellationToken.None);
        await db.ScalarAsync($"INSERT INTO schema_version (version, applied_at) VALUES ({Migrations.Latest + 1}, 0);");

        using var older = new KasuMetaDatabase(db.DbPath, db.Time, NullLogger<KasuMetaDatabase>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => older.EnsureInitializedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_first_use_initializes_once()
    {
        using var db = new TestDatabase();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.Database.EnsureInitializedAsync(CancellationToken.None)));

        Assert.Equal((long)Migrations.All.Count, await db.ScalarAsync("SELECT COUNT(*) FROM schema_version;"));
    }

    [Fact]
    public async Task Fts5_probe_detects_the_bundled_sqlite()
    {
        // The e_sqlite3 bundle is built with FTS5, like the server's. The live check on 12.1
        // is the startup log line ("FTS5 available").
        using var db = new TestDatabase();
        await db.Database.EnsureInitializedAsync(CancellationToken.None);

        Assert.True(db.Database.SupportsFts5);
    }

    [Fact]
    public async Task Fts5_probe_reports_false_instead_of_throwing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // A table already holding the probe's name makes the CREATE fail: that must read as
        // "no FTS5", never as an exception that stops the plugin.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE temp.kasumeta_fts5_probe (x);";
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await KasuMetaDatabase.ProbeFts5Async(connection, CancellationToken.None));
    }
}
