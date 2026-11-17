using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>
/// The FTS5 index over <c>search_name</c>: derived data, rebuilt from that table whenever it is
/// missing or its definition changes, and left out entirely when the server's SQLite has no FTS5
/// (search then falls back to substring matching on <c>search_name</c>).
/// </summary>
internal static class SearchIndex
{
    /// <summary>
    /// Version of the index definition below. Raise it when the tokenizer or the columns change;
    /// the index is then dropped and rebuilt on the next start.
    /// </summary>
    internal const string Version = "1";

    private const string VersionKey = "search_fts_version";

    // unicode61 folds case and, with remove_diacritics 2, accents: "cabana" finds "cabaña".
    // External content: the text lives in search_name only; the index stores tokens.
    private const string CreateSql = """
        CREATE VIRTUAL TABLE localized_search USING fts5(
          name,
          content = 'search_name',
          content_rowid = 'id',
          tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER search_name_fts_insert AFTER INSERT ON search_name BEGIN
          INSERT INTO localized_search (rowid, name) VALUES (new.id, new.name);
        END;

        CREATE TRIGGER search_name_fts_delete AFTER DELETE ON search_name BEGIN
          INSERT INTO localized_search (localized_search, rowid, name) VALUES ('delete', old.id, old.name);
        END;

        CREATE TRIGGER search_name_fts_update AFTER UPDATE ON search_name BEGIN
          INSERT INTO localized_search (localized_search, rowid, name) VALUES ('delete', old.id, old.name);
          INSERT INTO localized_search (rowid, name) VALUES (new.id, new.name);
        END;

        INSERT INTO localized_search (localized_search) VALUES ('rebuild');
        """;

    private const string DropTriggersSql = """
        DROP TRIGGER IF EXISTS search_name_fts_insert;
        DROP TRIGGER IF EXISTS search_name_fts_delete;
        DROP TRIGGER IF EXISTS search_name_fts_update;
        """;

    /// <summary>Makes the index match this build; returns whether full-text search is usable.</summary>
    internal static async Task<bool> EnsureAsync(SqliteConnection connection, bool supportsFts5, CancellationToken cancellationToken)
    {
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var current = await ScalarAsync(
                    connection, transaction, "SELECT value FROM meta WHERE key = $key;", cancellationToken, ("$key", VersionKey))
                .ConfigureAwait(false) as string;

            if (!supportsFts5)
            {
                // A database that came from a server with FTS5: its triggers would now fail every
                // name write ("no such module: fts5"). Drop them; the index is rebuilt if FTS5 returns.
                await ExecuteAsync(connection, transaction, DropTriggersSql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                        connection, transaction, "DELETE FROM meta WHERE key = $key;", cancellationToken, ("$key", VersionKey))
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (current != Version)
            {
                await ExecuteAsync(connection, transaction, DropTriggersSql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "DROP TABLE IF EXISTS localized_search;", cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, CreateSql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                        connection,
                        transaction,
                        "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
                        cancellationToken,
                        ("$key", VersionKey),
                        ("$value", Version))
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
