using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuMeta.Storage;

/// <summary>
/// Forward-only schema migrations. Never edit a shipped migration: add a new one.
/// </summary>
internal static class Migrations
{
    /// <summary>Gets every migration, in order. Versions start at 1 with no gaps.</summary>
    internal static IReadOnlyList<(int Version, string Sql)> All { get; } =
    [
        (1, """
            CREATE TABLE localized_field (
              item_id    TEXT    NOT NULL,           -- Jellyfin item GUID, "N" format
              locale     TEXT    NOT NULL,           -- BCP-47, e.g. 'es-MX'
              field      TEXT    NOT NULL,           -- MetadataFields
              value      TEXT    NOT NULL,
              source     TEXT    NOT NULL,           -- MetadataSources
              locked     INTEGER NOT NULL DEFAULT 0, -- 1 = manual override
              updated_at INTEGER NOT NULL,           -- unix seconds
              PRIMARY KEY (item_id, locale, field)
            ) WITHOUT ROWID;

            CREATE TABLE localized_image (
              item_id    TEXT    NOT NULL,
              locale     TEXT    NOT NULL,
              image_type TEXT    NOT NULL,           -- LocalizedImageTypes
              url        TEXT    NOT NULL,
              source     TEXT    NOT NULL,
              locked     INTEGER NOT NULL DEFAULT 0,
              updated_at INTEGER NOT NULL,
              PRIMARY KEY (item_id, locale, image_type)
            ) WITHOUT ROWID;

            CREATE TABLE fetch_state (
              item_id      TEXT PRIMARY KEY,
              provider_ids TEXT NOT NULL,            -- JSON object, keys sorted
              last_fetch   INTEGER,                  -- unix seconds, NULL = never
              status       TEXT NOT NULL,            -- 'pending' | 'ok' | 'error'
              last_error   TEXT
            );

            CREATE INDEX ix_fetch_state_last_fetch ON fetch_state (last_fetch);
            """),

        // Phase 2: image sizes, and which fetch logic produced a state. Rows from Phase 1 are
        // fetch version 1 (no images), so the backfill re-queues them once.
        (2, """
            ALTER TABLE localized_image ADD COLUMN width INTEGER;
            ALTER TABLE localized_image ADD COLUMN height INTEGER;
            ALTER TABLE fetch_state ADD COLUMN fetch_version INTEGER NOT NULL DEFAULT 1;
            """),

        // Phase 3: every localized name in one plain table, kept exact by triggers on
        // localized_field, so no write path can forget it. Search reads this table; the FTS5
        // index over it is derived and optional (SearchIndex), which is why it isn't created
        // here: a server whose SQLite lacks FTS5 must still migrate.
        (3, """
            -- The item's kind (Movie, Series, Season, Episode), so search can filter and rank by
            -- it without asking Jellyfin about every candidate. NULL until the next fetch.
            ALTER TABLE fetch_state ADD COLUMN kind TEXT;

            CREATE TABLE meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE search_name (
              id      INTEGER PRIMARY KEY,           -- rowid, the FTS index's key
              item_id TEXT NOT NULL,
              locale  TEXT NOT NULL,
              name    TEXT NOT NULL,
              UNIQUE (item_id, locale)
            );

            CREATE TRIGGER localized_field_name_insert AFTER INSERT ON localized_field
            WHEN new.field = 'name' BEGIN
              INSERT INTO search_name (item_id, locale, name) VALUES (new.item_id, new.locale, new.value);
            END;

            CREATE TRIGGER localized_field_name_update AFTER UPDATE OF value ON localized_field
            WHEN new.field = 'name' BEGIN
              UPDATE search_name SET name = new.value WHERE item_id = new.item_id AND locale = new.locale;
            END;

            CREATE TRIGGER localized_field_name_delete AFTER DELETE ON localized_field
            WHEN old.field = 'name' BEGIN
              DELETE FROM search_name WHERE item_id = old.item_id AND locale = old.locale;
            END;

            INSERT INTO search_name (item_id, locale, name)
            SELECT item_id, locale, value FROM localized_field WHERE field = 'name';
            """),

        // Phase 6: uploaded images, the item's original language (the last-resort fallback),
        // and the admin's genre and tag dictionary. The blob is the image row's last column and
        // no list query selects it, so reading rows never touches its overflow pages.
        (4, """
            ALTER TABLE localized_image ADD COLUMN localized_image_file BLOB;
            ALTER TABLE fetch_state ADD COLUMN original_language TEXT;

            CREATE TABLE term_translation (
              kind       TEXT    NOT NULL,                -- TermKinds
              term       TEXT    NOT NULL COLLATE NOCASE, -- the provider's (English) term
              locale     TEXT    NOT NULL,                -- BCP-47
              value      TEXT    NOT NULL,
              updated_at INTEGER NOT NULL,
              PRIMARY KEY (kind, term, locale)
            ) WITHOUT ROWID;
            """),
    ];

    /// <summary>Gets the newest schema version this build knows.</summary>
    internal static int Latest => All[^1].Version;

    /// <summary>Brings the database up to <see cref="Latest"/>; returns the version it ends at.</summary>
    internal static async Task<int> MigrateAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL);";
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int current;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = Convert.ToInt32(
                await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (current > Latest)
        {
            // A newer plugin wrote this file and was then downgraded. Don't guess at its schema.
            throw new InvalidOperationException(
                $"KasuMeta database is at schema {current}, newer than this plugin's {Latest}. Install a newer KasuMeta.");
        }

        foreach (var (version, sql) in All.Where(m => m.Version > current))
        {
            var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var migrate = connection.CreateCommand())
                {
                    migrate.Transaction = transaction;
                    migrate.CommandText = sql;
                    await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES ($v, $t);";
                    record.Parameters.AddWithValue("$v", version);
                    record.Parameters.AddWithValue("$t", now.ToUnixTimeSeconds());
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            StoreLog.Migrated(logger, version);
        }

        return Latest;
    }
}
