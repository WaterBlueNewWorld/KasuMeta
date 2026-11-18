using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.KasuMeta.Library;
using Jellyfin.Plugin.KasuMeta.Search;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>The SQLite-backed <see cref="ILocalizedMetadataStore"/>.</summary>
public sealed class SqliteLocalizedMetadataStore : ILocalizedMetadataStore
{
    /// <summary>Ids per IN (...) list; far below SQLite's parameter limit.</summary>
    internal const int ChunkSize = 500;

    private const string UpsertFieldSql = """
        INSERT INTO localized_field (item_id, locale, field, value, source, locked, updated_at)
        VALUES ($item, $locale, $key, $value, $source, $locked, $now)
        ON CONFLICT (item_id, locale, field) DO UPDATE SET
          value = excluded.value, source = excluded.source,
          locked = excluded.locked, updated_at = excluded.updated_at
        WHERE localized_field.locked = 0 OR excluded.locked = 1;
        """;

    private const string UpsertImageSql = """
        INSERT INTO localized_image (item_id, locale, image_type, url, source, locked, updated_at, width, height)
        VALUES ($item, $locale, $key, $value, $source, $locked, $now, $width, $height)
        ON CONFLICT (item_id, locale, image_type) DO UPDATE SET
          url = excluded.url, source = excluded.source,
          locked = excluded.locked, updated_at = excluded.updated_at,
          width = excluded.width, height = excluded.height
        WHERE localized_image.locked = 0 OR excluded.locked = 1;
        """;

    // An admin's write: no WHERE, so it replaces a locked row too.
    private const string ManualFieldSql = """
        INSERT INTO localized_field (item_id, locale, field, value, source, locked, updated_at)
        VALUES ($item, $locale, $key, $value, $source, $locked, $now)
        ON CONFLICT (item_id, locale, field) DO UPDATE SET
          value = excluded.value, source = excluded.source,
          locked = excluded.locked, updated_at = excluded.updated_at;
        """;

    private const string ManualImageSql = """
        INSERT INTO localized_image (item_id, locale, image_type, url, source, locked, updated_at, width, height)
        VALUES ($item, $locale, $key, $value, $source, $locked, $now, $width, $height)
        ON CONFLICT (item_id, locale, image_type) DO UPDATE SET
          url = excluded.url, source = excluded.source,
          locked = excluded.locked, updated_at = excluded.updated_at,
          width = excluded.width, height = excluded.height;
        """;

    private readonly KasuMetaDatabase _database;
    private readonly TimeProvider _time;

    public SqliteLocalizedMetadataStore(KasuMetaDatabase database, TimeProvider time)
    {
        _database = database;
        _time = time;
    }

    /// <inheritdoc />
    public Task<int> UpsertFieldsAsync(IReadOnlyCollection<LocalizedField> fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var f in fields)
        {
            RequireKnown(f.Field, MetadataFields.All, nameof(LocalizedField.Field));
            RequireLocale(f.Locale);
        }

        return UpsertAsync(
            UpsertFieldSql,
            fields.Select(f => (f.ItemId, f.Locale, f.Field, f.Value, f.Source, f.Locked, (int?)null, (int?)null)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ReplaceProviderFieldsAsync(
        Guid itemId,
        string source,
        IReadOnlyCollection<LocalizedField> fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        foreach (var f in fields)
        {
            if (f.ItemId != itemId || f.Source != source || f.Locked)
            {
                throw new ArgumentException("Every row must be an unlocked row of this item and source.", nameof(fields));
            }

            RequireKnown(f.Field, MetadataFields.All, nameof(LocalizedField.Field));
            RequireLocale(f.Locale);
        }

        return UpsertAsync(
            UpsertFieldSql,
            fields.Select(f => (f.ItemId, f.Locale, f.Field, f.Value, f.Source, f.Locked, (int?)null, (int?)null)),
            cancellationToken,
            replaceUnlocked: ("localized_field", itemId, source));
    }

    /// <inheritdoc />
    public Task<int> UpsertImagesAsync(IReadOnlyCollection<LocalizedImage> images, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        foreach (var i in images)
        {
            RequireKnown(i.ImageType, LocalizedImageTypes.All, nameof(LocalizedImage.ImageType));
            RequireLocale(i.Locale);
        }

        return UpsertAsync(
            UpsertImageSql,
            images.Select(i => (i.ItemId, i.Locale, i.ImageType, i.Url, i.Source, i.Locked, i.Width, i.Height)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ReplaceProviderImagesAsync(
        Guid itemId,
        string source,
        IReadOnlyCollection<LocalizedImage> images,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        foreach (var i in images)
        {
            if (i.ItemId != itemId || i.Source != source || i.Locked)
            {
                throw new ArgumentException("Every row must be an unlocked row of this item and source.", nameof(images));
            }

            RequireKnown(i.ImageType, LocalizedImageTypes.All, nameof(LocalizedImage.ImageType));
            RequireLocale(i.Locale);
        }

        return UpsertAsync(
            UpsertImageSql,
            images.Select(i => (i.ItemId, i.Locale, i.ImageType, i.Url, i.Source, i.Locked, i.Width, i.Height)),
            cancellationToken,
            replaceUnlocked: ("localized_image", itemId, source));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LocalizedField>> GetFieldsAsync(
        IReadOnlyCollection<Guid> itemIds,
        IReadOnlyCollection<string>? locales,
        CancellationToken cancellationToken)
    {
        return QueryByItemsAsync(
            "SELECT item_id, locale, field, value, source, locked, updated_at FROM localized_field",
            itemIds,
            locales,
            r => new LocalizedField(
                ParseId(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt64(5) != 0)
            {
                UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)),
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LocalizedImage>> GetImagesAsync(
        IReadOnlyCollection<Guid> itemIds,
        IReadOnlyCollection<string>? locales,
        CancellationToken cancellationToken)
    {
        return QueryByItemsAsync(
            "SELECT item_id, locale, image_type, url, source, locked, updated_at, width, height FROM localized_image",
            itemIds,
            locales,
            r => new LocalizedImage(
                ParseId(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt64(5) != 0,
                r.IsDBNull(7) ? null : r.GetInt32(7),
                r.IsDBNull(8) ? null : r.GetInt32(8))
            {
                UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)),
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetFetchStateAsync(FetchState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO fetch_state (item_id, provider_ids, last_fetch, status, last_error, fetch_version, kind)
                VALUES ($item, $ids, $last, $status, $error, $version, $kind)
                ON CONFLICT (item_id) DO UPDATE SET
                  provider_ids = excluded.provider_ids, last_fetch = excluded.last_fetch,
                  status = excluded.status, last_error = excluded.last_error,
                  fetch_version = excluded.fetch_version, kind = excluded.kind;
                """;
            command.Parameters.AddWithValue("$item", FormatId(state.ItemId));
            command.Parameters.AddWithValue("$ids", SerializeProviderIds(state.ProviderIds));
            command.Parameters.AddWithValue("$last", (object?)state.LastFetch?.ToUnixTimeSeconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", FormatStatus(state.Status));
            command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", state.FetchVersion);
            command.Parameters.AddWithValue("$kind", (object?)state.Kind?.ToString() ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<FetchState?> GetFetchStateAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT provider_ids, last_fetch, status, last_error, fetch_version, kind FROM fetch_state WHERE item_id = $item;";
            command.Parameters.AddWithValue("$item", FormatId(itemId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new FetchState(
                itemId,
                DeserializeProviderIds(reader.GetString(0)),
                reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                ParseStatus(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : ParseKind(reader.GetString(5)));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetStaleItemIdsAsync(
        DateTimeOffset fetchedBefore,
        int limit,
        CancellationToken cancellationToken,
        bool includeErrors = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            // Skipped rows wait for a provider-id change instead (ItemUpdated), not for age.
            command.CommandText = """
                SELECT item_id FROM fetch_state
                WHERE status <> 'skipped'
                  AND (last_fetch IS NULL OR last_fetch < $before OR ($errors = 1 AND status = 'error'))
                ORDER BY COALESCE(last_fetch, 0), item_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$errors", includeErrors ? 1 : 0);
            command.Parameters.AddWithValue("$before", fetchedBefore.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$limit", limit);
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(ParseId(reader.GetString(0)));
            }

            return ids;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<Guid>> GetKnownItemIdsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken,
        int minFetchVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var known = new HashSet<Guid>();
        if (itemIds.Count == 0)
        {
            return known;
        }

        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            foreach (var chunk in itemIds.Distinct().Chunk(ChunkSize))
            {
                await using var command = connection.CreateCommand();
                var sql = new StringBuilder("SELECT item_id FROM fetch_state WHERE fetch_version >= $min AND item_id IN (");
                AppendParameters(command, sql, "$i", chunk.Select(FormatId));
                command.CommandText = sql.Append(");").ToString();
                command.Parameters.AddWithValue("$min", minFetchVersion);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    known.Add(ParseId(reader.GetString(0)));
                }
            }
        }

        return known;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NameHit>> SearchNamesAsync(
        SearchQuery query,
        IReadOnlySet<ItemKind> kinds,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);
        var hits = new List<NameHit>();
        if (query.IsEmpty || kinds.Count == 0)
        {
            return hits;
        }

        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            var match = _database.SearchIndexReady ? query.ToMatchExpression() : null;
            var sql = new StringBuilder();
            IEnumerable<string> substrings;
            if (match is not null)
            {
                // Word terms through the index (case and accents folded, last term a prefix),
                // best BM25 first; terms in scripts without spaces as substrings on the name.
                sql.Append("""
                    SELECT s.item_id, s.locale, s.name, localized_search.rank, f.kind
                    FROM localized_search
                    JOIN search_name s ON s.id = localized_search.rowid
                    LEFT JOIN fetch_state f ON f.item_id = s.item_id
                    WHERE localized_search MATCH $match
                    """);
                command.Parameters.AddWithValue("$match", match);
                substrings = query.SubstringTerms;
            }
            else
            {
                // No index (or no word terms): every term as a substring, shortest names first.
                sql.Append("""
                    SELECT s.item_id, s.locale, s.name, 0.0, f.kind
                    FROM search_name s
                    LEFT JOIN fetch_state f ON f.item_id = s.item_id
                    WHERE 1 = 1
                    """);
                substrings = query.WordTerms.Concat(query.SubstringTerms);
            }

            var index = 0;
            foreach (var term in substrings)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"$like{index++}");
                sql.Append(" AND s.name LIKE ").Append(name).Append(" ESCAPE '\\'");
                command.Parameters.AddWithValue(name, SearchQuery.ToLikePattern(term));
            }

            // Kinds not wanted never reach the candidate cap. A kind not recorded yet (an item
            // fetched before kinds were stored) passes; the caller checks it against the library.
            sql.Append(" AND (f.kind IS NULL OR f.kind IN (");
            var kindIndex = 0;
            foreach (var kind in kinds)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"$kind{kindIndex}");
                sql.Append(kindIndex++ == 0 ? string.Empty : ", ").Append(name);
                command.Parameters.AddWithValue(name, kind.ToString());
            }

            sql.Append("))");
            sql.Append(match is not null ? " ORDER BY localized_search.rank" : " ORDER BY length(s.name), s.name");
            sql.Append(" LIMIT $limit;");
            command.Parameters.AddWithValue("$limit", maxCandidates);
            command.CommandText = sql.ToString();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(new NameHit(
                    ParseId(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : ParseKind(reader.GetString(4))));
            }
        }

        return hits;
    }

    /// <inheritdoc />
    public async Task<int> ApplyManualEditAsync(ManualEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        RequireLocale(edit.Locale);
        foreach (var f in edit.SetFields)
        {
            RequireManualRow(f.ItemId, f.Locale, f.Source, edit);
            RequireKnown(f.Field, MetadataFields.All, nameof(LocalizedField.Field));
        }

        foreach (var i in edit.SetImages)
        {
            RequireManualRow(i.ItemId, i.Locale, i.Source, edit);
            RequireKnown(i.ImageType, LocalizedImageTypes.All, nameof(LocalizedImage.ImageType));
        }

        foreach (var field in edit.RemoveFields)
        {
            RequireKnown(field, MetadataFields.All, nameof(LocalizedField.Field));
        }

        foreach (var type in edit.RemoveImages)
        {
            RequireKnown(type, LocalizedImageTypes.All, nameof(LocalizedImage.ImageType));
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var changed = 0;
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                // Table and column names are this class's own literals, never input.
                foreach (var (table, column, keys) in new[]
                         {
                             ("localized_field", "field", edit.RemoveFields),
                             ("localized_image", "image_type", edit.RemoveImages),
                         })
                {
                    foreach (var key in keys.Distinct(StringComparer.Ordinal))
                    {
                        await using var delete = connection.CreateCommand();
                        delete.Transaction = transaction;
                        delete.CommandText = $"DELETE FROM {table} WHERE item_id = $item AND locale = $locale AND {column} = $key;";
                        delete.Parameters.AddWithValue("$item", FormatId(edit.ItemId));
                        delete.Parameters.AddWithValue("$locale", edit.Locale);
                        delete.Parameters.AddWithValue("$key", key);
                        changed += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                changed += await WriteRowsAsync(
                        connection,
                        transaction,
                        ManualFieldSql,
                        edit.SetFields.Select(f => (f.ItemId, f.Locale, f.Field, f.Value, f.Source, f.Locked, (int?)null, (int?)null)),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                changed += await WriteRowsAsync(
                        connection,
                        transaction,
                        ManualImageSql,
                        edit.SetImages.Select(i => (i.ItemId, i.Locale, i.ImageType, i.Url, i.Source, i.Locked, i.Width, i.Height)),
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return changed;
    }

    /// <inheritdoc />
    public async Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var table in new[] { "localized_field", "localized_image", "fetch_state" })
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {table} WHERE item_id = $item;";
                    command.Parameters.AddWithValue("$item", FormatId(itemId));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static string FormatId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Sorted keys, so equal id sets always give the same JSON and compare equal.</summary>
    internal static string SerializeProviderIds(IReadOnlyDictionary<string, string> providerIds) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(
            providerIds.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            StringComparer.Ordinal));

    private static Dictionary<string, string> DeserializeProviderIds(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private static Guid ParseId(string value) => Guid.ParseExact(value, "N");

    /// <summary>Reads a stored kind; an unknown name (written by a newer build) reads as "not recorded".</summary>
    private static ItemKind? ParseKind(string value) =>
        Enum.TryParse<ItemKind>(value, out var kind) && Enum.IsDefined(kind) ? kind : null;

    private static string FormatStatus(FetchStatus status) => status switch
    {
        FetchStatus.Pending => "pending",
        FetchStatus.Ok => "ok",
        FetchStatus.Error => "error",
        FetchStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static FetchStatus ParseStatus(string value) => value switch
    {
        "pending" => FetchStatus.Pending,
        "ok" => FetchStatus.Ok,
        "error" => FetchStatus.Error,
        "skipped" => FetchStatus.Skipped,
        _ => throw new InvalidDataException($"Unknown fetch status '{value}'."),
    };

    private static void RequireKnown(string value, IReadOnlySet<string> known, string what)
    {
        if (!known.Contains(value))
        {
            throw new ArgumentException($"Unknown {what} '{value}'.", what);
        }
    }

    private static void RequireManualRow(Guid itemId, string locale, string source, ManualEdit edit)
    {
        if (itemId != edit.ItemId || locale != edit.Locale || source != MetadataSources.Manual)
        {
            throw new ArgumentException("Every row must be a manual row of the edit's item and locale.", nameof(edit));
        }
    }

    private static void RequireLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            throw new ArgumentException("Locale is required.", nameof(locale));
        }
    }

    private async Task<int> UpsertAsync(
        string sql,
        IEnumerable<(Guid ItemId, string Locale, string Key, string Value, string Source, bool Locked, int? Width, int? Height)> rows,
        CancellationToken cancellationToken,
        (string Table, Guid ItemId, string Source)? replaceUnlocked = null)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        int written;
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (replaceUnlocked is { } replace)
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    // Table comes from this class's own literals, never from input.
                    delete.CommandText =
                        $"DELETE FROM {replace.Table} WHERE item_id = $item AND source = $source AND locked = 0;";
                    delete.Parameters.AddWithValue("$item", FormatId(replace.ItemId));
                    delete.Parameters.AddWithValue("$source", replace.Source);
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                written = await WriteRowsAsync(connection, transaction, sql, rows, now, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return written;
    }

    private static async Task<int> WriteRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IEnumerable<(Guid ItemId, string Locale, string Key, string Value, string Source, bool Locked, int? Width, int? Height)> rows,
        long now,
        CancellationToken cancellationToken)
    {
        var written = 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var item = command.Parameters.Add("$item", SqliteType.Text);
        var locale = command.Parameters.Add("$locale", SqliteType.Text);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var locked = command.Parameters.Add("$locked", SqliteType.Integer);
        var hasSize = sql.Contains("$width", StringComparison.Ordinal);
        var width = hasSize ? command.Parameters.Add("$width", SqliteType.Integer) : null;
        var height = hasSize ? command.Parameters.Add("$height", SqliteType.Integer) : null;
        command.Parameters.AddWithValue("$now", now);

        foreach (var row in rows)
        {
            item.Value = FormatId(row.ItemId);
            locale.Value = row.Locale;
            key.Value = row.Key;
            value.Value = row.Value;
            source.Value = row.Source;
            locked.Value = row.Locked ? 1 : 0;
            if (width is not null && height is not null)
            {
                width.Value = (object?)row.Width ?? DBNull.Value;
                height.Value = (object?)row.Height ?? DBNull.Value;
            }

            written += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private async Task<IReadOnlyList<T>> QueryByItemsAsync<T>(
        string select,
        IReadOnlyCollection<Guid> itemIds,
        IReadOnlyCollection<string>? locales,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var results = new List<T>();
        if (itemIds.Count == 0 || locales is { Count: 0 })
        {
            return results;
        }

        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            foreach (var chunk in itemIds.Distinct().Chunk(ChunkSize))
            {
                await using var command = connection.CreateCommand();
                var sql = new StringBuilder(select).Append(" WHERE item_id IN (");
                AppendParameters(command, sql, "$i", chunk.Select(FormatId));
                sql.Append(')');
                if (locales is not null)
                {
                    sql.Append(" AND locale IN (");
                    AppendParameters(command, sql, "$l", locales.Distinct(StringComparer.Ordinal));
                    sql.Append(')');
                }

                command.CommandText = sql.Append(';').ToString();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(map(reader));
                }
            }
        }

        return results;
    }

    private static void AppendParameters(SqliteCommand command, StringBuilder sql, string prefix, IEnumerable<string> values)
    {
        var index = 0;
        foreach (var value in values)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{prefix}{index}");
            sql.Append(index == 0 ? string.Empty : ", ").Append(name);
            command.Parameters.AddWithValue(name, value);
            index++;
        }
    }
}
