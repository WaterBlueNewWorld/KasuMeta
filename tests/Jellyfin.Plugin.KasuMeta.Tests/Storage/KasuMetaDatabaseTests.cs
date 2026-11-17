using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;
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
    public async Task A_phase_1_database_upgrades_in_place()
    {
        using var db = new TestDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(db.DbPath)!);
        await using (var v1 = new SqliteConnection($"Data Source={db.DbPath};Pooling=False"))
        {
            await v1.OpenAsync();
            await using var command = v1.CreateCommand();
            const string emptyIds = "{}";
            command.CommandText = $"""
                CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL);
                {Migrations.All[0].Sql}
                INSERT INTO schema_version VALUES (1, 0);
                INSERT INTO fetch_state (item_id, provider_ids, last_fetch, status) VALUES ('{new string('a', 32)}', '{emptyIds}', 1, 'ok');
                INSERT INTO localized_image (item_id, locale, image_type, url, source, locked, updated_at)
                  VALUES ('{new string('a', 32)}', 'es-MX', 'Primary', 'https://x/p.jpg', 'manual', 1, 1);
                INSERT INTO localized_field (item_id, locale, field, value, source, locked, updated_at)
                  VALUES ('{new string('a', 32)}', 'es-MX', 'name', 'La cabaña del terror', 'tmdb', 0, 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await db.Database.EnsureInitializedAsync(CancellationToken.None);

        var id = Guid.Parse(new string('a', 32));
        Assert.Equal(Migrations.Latest, db.Database.SchemaVersion);
        Assert.Equal(1, (await db.Store.GetFetchStateAsync(id, CancellationToken.None))!.FetchVersion);
        var image = Assert.Single(await db.Store.GetImagesAsync([id], null, CancellationToken.None));
        Assert.Equal(("https://x/p.jpg", true, (int?)null), (image.Url, image.Locked, image.Width));

        // A name stored before search existed is searchable after the upgrade.
        var hit = Assert.Single(await db.Store.SearchNamesAsync(
            SearchQuery.Parse("cabana"), Enum.GetValues<ItemKind>().ToHashSet(), 10, CancellationToken.None));
        Assert.Equal((id, "es-MX", (ItemKind?)null), (hit.ItemId, hit.Locale, hit.Kind));
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
