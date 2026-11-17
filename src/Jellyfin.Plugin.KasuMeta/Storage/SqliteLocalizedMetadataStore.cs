using System.Globalization;
using System.Text;
using System.Text.Json;
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
        INSERT INTO localized_image (item_id, locale, image_type, url, source, locked, updated_at)
        VALUES ($item, $locale, $key, $value, $source, $locked, $now)
        ON CONFLICT (item_id, locale, image_type) DO UPDATE SET
          url = excluded.url, source = excluded.source,
          locked = excluded.locked, updated_at = excluded.updated_at
        WHERE localized_image.locked = 0 OR excluded.locked = 1;
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
            fields.Select(f => (f.ItemId, f.Locale, f.Field, f.Value, f.Source, f.Locked)),
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
            fields.Select(f => (f.ItemId, f.Locale, f.Field, f.Value, f.Source, f.Locked)),
            cancellationToken,
            replaceUnlocked: (itemId, source));
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
            images.Select(i => (i.ItemId, i.Locale, i.ImageType, i.Url, i.Source, i.Locked)),
            cancellationToken);
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
            "SELECT item_id, locale, image_type, url, source, locked, updated_at FROM localized_image",
            itemIds,
            locales,
            r => new LocalizedImage(
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
    public async Task SetFetchStateAsync(FetchState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO fetch_state (item_id, provider_ids, last_fetch, status, last_error)
                VALUES ($item, $ids, $last, $status, $error)
                ON CONFLICT (item_id) DO UPDATE SET
                  provider_ids = excluded.provider_ids, last_fetch = excluded.last_fetch,
                  status = excluded.status, last_error = excluded.last_error;
                """;
            command.Parameters.AddWithValue("$item", FormatId(state.ItemId));
            command.Parameters.AddWithValue("$ids", SerializeProviderIds(state.ProviderIds));
            command.Parameters.AddWithValue("$last", (object?)state.LastFetch?.ToUnixTimeSeconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", FormatStatus(state.Status));
            command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
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
                "SELECT provider_ids, last_fetch, status, last_error FROM fetch_state WHERE item_id = $item;";
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
                reader.IsDBNull(3) ? null : reader.GetString(3));
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
    public async Task<IReadOnlySet<Guid>> GetKnownItemIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
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
                var sql = new StringBuilder("SELECT item_id FROM fetch_state WHERE item_id IN (");
                AppendParameters(command, sql, "$i", chunk.Select(FormatId));
                command.CommandText = sql.Append(");").ToString();
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

    private static void RequireLocale(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            throw new ArgumentException("Locale is required.", nameof(locale));
        }
    }

    private async Task<int> UpsertAsync(
        string sql,
        IEnumerable<(Guid ItemId, string Locale, string Key, string Value, string Source, bool Locked)> rows,
        CancellationToken cancellationToken,
        (Guid ItemId, string Source)? replaceUnlocked = null)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var written = 0;
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
                    delete.CommandText =
                        "DELETE FROM localized_field WHERE item_id = $item AND source = $source AND locked = 0;";
                    delete.Parameters.AddWithValue("$item", FormatId(replace.ItemId));
                    delete.Parameters.AddWithValue("$source", replace.Source);
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                var item = command.Parameters.Add("$item", SqliteType.Text);
                var locale = command.Parameters.Add("$locale", SqliteType.Text);
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var value = command.Parameters.Add("$value", SqliteType.Text);
                var source = command.Parameters.Add("$source", SqliteType.Text);
                var locked = command.Parameters.Add("$locked", SqliteType.Integer);
                command.Parameters.AddWithValue("$now", now);

                foreach (var row in rows)
                {
                    item.Value = FormatId(row.ItemId);
                    locale.Value = row.Locale;
                    key.Value = row.Key;
                    value.Value = row.Value;
                    source.Value = row.Source;
                    locked.Value = row.Locked ? 1 : 0;
                    written += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
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
