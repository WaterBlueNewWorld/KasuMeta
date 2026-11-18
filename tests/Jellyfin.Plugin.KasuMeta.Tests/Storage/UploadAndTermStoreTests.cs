using Jellyfin.Plugin.KasuMeta.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.KasuMeta.Tests.Storage;

/// <summary>Migration 4: uploaded image files, the original language, and the term dictionary.</summary>
public sealed class UploadAndTermStoreTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid Id = Guid.Parse("66666666666666666666666666666666");
    private static readonly Guid Other = Guid.Parse("77777777777777777777777777777777");

    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_schema_has_the_upload_column_and_the_dictionary()
    {
        await _db.Database.EnsureInitializedAsync(None);

        Assert.Equal(4L, await _db.ScalarAsync("SELECT MAX(version) FROM schema_version;"));
        Assert.Equal(1L, await _db.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('localized_image') WHERE name = 'localized_image_file' AND type = 'BLOB';"));
        Assert.Equal(1L, await _db.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('fetch_state') WHERE name = 'original_language';"));
        Assert.Equal(1L, await _db.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'term_translation';"));
    }

    [Fact]
    public async Task An_upload_round_trips_and_list_reads_only_flag_it()
    {
        var file = Enumerable.Range(0, 5_000).Select(i => (byte)i).ToArray();

        await _db.Store.SetUploadedImageAsync(
            new LocalizedImage(Id, "es-MX", "Logo", "ignored", MetadataSources.Manual, Locked: true, Width: 640, Height: 360),
            file,
            None);

        var row = Assert.Single(await _db.Store.GetImagesAsync([Id], null, None));
        Assert.Equal((true, string.Empty, 640, true), (row.HasFile, row.Url, row.Width!.Value, row.Locked));
        var stored = await _db.Store.GetImageFileAsync(Id, "es-MX", "Logo", None);
        Assert.Equal(file, stored!.Value.Data);
        Assert.Equal(_db.Time.Now.ToUnixTimeSeconds(), stored.Value.UpdatedAt.ToUnixTimeSeconds());
        Assert.Null(await _db.Store.GetImageFileAsync(Id, "es-ES", "Logo", None));
    }

    [Fact]
    public async Task A_provider_write_over_an_unlocked_upload_drops_the_file()
    {
        await _db.Store.SetUploadedImageAsync(new LocalizedImage(Id, "es-MX", "Logo", string.Empty, MetadataSources.Manual, Locked: false), [1, 2, 3], None);

        await _db.Store.ReplaceProvidersAsync(
            Id,
            [MetadataSources.Tmdb],
            [],
            [new LocalizedImage(Id, "es-MX", "Logo", "https://image.tmdb.org/t/p/original/l.png", MetadataSources.Tmdb)],
            None);

        var row = Assert.Single(await _db.Store.GetImagesAsync([Id], null, None));
        Assert.Equal((false, MetadataSources.Tmdb), (row.HasFile, row.Source));
        Assert.Null(await _db.Store.GetImageFileAsync(Id, "es-MX", "Logo", None));
    }

    [Fact]
    public async Task A_locked_upload_survives_provider_writes()
    {
        await _db.Store.SetUploadedImageAsync(new LocalizedImage(Id, "es-MX", "Logo", string.Empty, MetadataSources.Manual, Locked: true), [1, 2, 3], None);

        await _db.Store.UpsertImagesAsync([new LocalizedImage(Id, "es-MX", "Logo", "https://x/l.png", MetadataSources.Tmdb)], None);

        Assert.True(Assert.Single(await _db.Store.GetImagesAsync([Id], null, None)).HasFile);
    }

    [Fact]
    public async Task Removing_the_image_removes_the_file()
    {
        await _db.Store.SetUploadedImageAsync(new LocalizedImage(Id, "es-MX", "Logo", string.Empty, MetadataSources.Manual), [1], None);

        await _db.Store.ApplyManualEditAsync(new ManualEdit(Id, "es-MX", [], [], [], ["Logo"]), None);

        Assert.Empty(await _db.Store.GetImagesAsync([Id], null, None));
        Assert.Null(await _db.Store.GetImageFileAsync(Id, "es-MX", "Logo", None));
    }

    [Fact]
    public async Task Only_manual_non_empty_uploads_are_accepted()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _db.Store.SetUploadedImageAsync(new LocalizedImage(Id, "es-MX", "Logo", string.Empty, MetadataSources.Tmdb), [1], None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _db.Store.SetUploadedImageAsync(new LocalizedImage(Id, "es-MX", "Logo", string.Empty, MetadataSources.Manual), [], None));
    }

    [Fact]
    public async Task The_original_language_is_stored_with_the_fetch_state()
    {
        await _db.Store.SetFetchStateAsync(new FetchState(Id, new Dictionary<string, string>(), null, FetchStatus.Ok, OriginalLanguage: "ja"), None);
        await _db.Store.SetFetchStateAsync(new FetchState(Other, new Dictionary<string, string>(), null, FetchStatus.Ok), None);

        Assert.Equal("ja", (await _db.Store.GetFetchStateAsync(Id, None))!.OriginalLanguage);
        var languages = await _db.Store.GetOriginalLanguagesAsync([Id, Other, Guid.NewGuid()], None);
        Assert.Equal(new Dictionary<Guid, string> { [Id] = "ja" }, languages);
    }

    [Fact]
    public async Task Dictionary_terms_are_set_replaced_and_removed_ignoring_case()
    {
        await _db.Store.SetTermsAsync(TermKinds.Genre, "es-MX", new Dictionary<string, string?> { ["Action"] = "Acción", ["Horror"] = "Horror" }, None);
        await _db.Store.SetTermsAsync(TermKinds.Genre, "es-MX", new Dictionary<string, string?> { ["ACTION"] = "Acción!", ["horror"] = null }, None);
        await _db.Store.SetTermsAsync(TermKinds.Tag, "es-MX", new Dictionary<string, string?> { ["Action"] = "Etiqueta" }, None);

        var genres = await _db.Store.GetTermsAsync(TermKinds.Genre, "es-MX", None);
        Assert.Equal([("Action", "Acción!")], genres.Select(t => (t.Term, t.Value)));
        Assert.Single(await _db.Store.GetTermsAsync(TermKinds.Tag, null, None));
        await Assert.ThrowsAsync<ArgumentException>(() => _db.Store.GetTermsAsync("studio", null, None));
    }

    [Fact]
    public async Task Terms_in_use_come_from_english_rows_counted_per_item()
    {
        await _db.Store.UpsertFieldsAsync(
            [
                new(Id, "en", MetadataFields.Genres, ListValues.Serialize(["Action", "Drama"])!, MetadataSources.Tvdb),
                new(Id, "en-US", MetadataFields.Genres, ListValues.Serialize(["action"])!, MetadataSources.Tmdb),
                new(Other, "en", MetadataFields.Genres, ListValues.Serialize(["Action"])!, MetadataSources.Tvdb),
                new(Other, "es-MX", MetadataFields.Genres, ListValues.Serialize(["Acción"])!, MetadataSources.Tmdb),
                new(Other, "en", MetadataFields.Tags, ListValues.Serialize(["Elf"])!, MetadataSources.Tvdb),
            ],
            None);

        var used = await _db.Store.GetTermsInUseAsync(TermKinds.Genre, None);

        Assert.Equal([("Action", 2), ("Drama", 1)], used.Select(u => (u.Term, u.Items)));
    }

    [Fact]
    public async Task A_version_3_file_migrates_with_its_rows()
    {
        // Build a v3 file by hand, as 1.1 left it.
        await using (var connection = new SqliteConnection($"Data Source={_db.DbPath};Pooling=False"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_db.DbPath)!);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL);";
            foreach (var (version, sql) in Migrations.All.Where(m => m.Version <= 3))
            {
                command.CommandText += sql + $"INSERT INTO schema_version VALUES ({version}, 0);";
            }

            command.CommandText += """
                INSERT INTO localized_image (item_id, locale, image_type, url, source, locked, updated_at)
                VALUES ('66666666666666666666666666666666', 'es-MX', 'Logo', 'https://x/l.png', 'tmdb', 0, 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var reopened = _db.Open();
        await reopened.EnsureInitializedAsync(None);
        var store = new SqliteLocalizedMetadataStore(reopened, _db.Time);

        var row = Assert.Single(await store.GetImagesAsync([Id], null, None));
        Assert.Equal(("https://x/l.png", false), (row.Url, row.HasFile));
    }
}
