# Changelog

All notable changes to KasuMeta. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Phase 3, search over localized names:
  - `GET /KasuMeta/Search?q=&locale=&limit=&types=`: names in every stored locale, so a title is
    found by its name in any enabled language; one result per item, reported under the caller's
    locale when it matched there; only items the caller may see.
  - Words go through an FTS5 index (case and accents folded, every word required, the last a
    prefix). Text in scripts written without spaces (Chinese, Japanese, Korean, Thai) matches as
    a substring, because the tokenizer makes one token of a whole title.
  - Ranking: exact name, then starts-with, then contains; movies and series before seasons
    before episodes; the caller's locale before its language's other regions before the rest.
  - Schema 3: `search_name` (every localized name, kept exact by triggers), the item's kind in
    the fetch state, and a `meta` table. The FTS5 index is derived: built at startup, rebuilt
    when its definition changes, and skipped on a SQLite without FTS5, where search falls back
    to substring matching.
  - Fetch version 3 (records the item's kind); older items are re-queued by the backfill.
  - `Info.Features` (`text`, `images`, `search`) so clients can tell what a build serves.
- Phase 2, localized images:
  - Posters (`Primary`), logos and backdrops per locale from TMDB, in the same single request
    per item (`append_to_response=translations,images`, `include_image_language` = the enabled
    languages). Seasons now use their details call to get their posters too.
  - TMDB images carry a region, so Mexican and Spanish posters differ, and Simplified and
    Traditional Chinese art stay apart. Choice: raster before SVG (Flutter can't draw SVG),
    then votes, then width.
  - Stored with their size (schema 2); served as `Images` per item, each type resolving its
    locale on its own; `Images` filter on the batch request.
  - Fetch version in the fetch state: items fetched by an older build (text only) are
    re-queued by the backfill, so an update reaches the whole library.
- Contract tests parse `api/openapi.yaml` and check each schema lists exactly its DTO's
  properties.

### Changed
- Batch `Fields`: an empty list now means "no fields" (images only), matching `Images`; absent
  still means all.

### Added (Phase 1)
- Phase 1 read path, complete:
  - Fetch queue and background worker (de-duplicating, 4 at a time); a fetch keeps the locales
    in the enabled locales' language groups and replaces the provider's unlocked rows, so a
    re-identified item loses the old title's translations; locked rows always stay.
  - Library hooks: ItemAdded/ItemUpdated queue the item (skipped when the provider ids didn't
    change), ItemRemoved deletes its rows.
  - Scheduled tasks under Dashboard › Scheduled Tasks › KasuMeta: "Fetch missing translations"
    (startup + daily) and "Refresh stale translations" (daily; also retries failures).
  - `POST /KasuMeta/Items/Batch` (≤ 200 ids, optional field filter) and
    `GET /KasuMeta/Items/{itemId}?locale=`, resolving the locale per field and serving only
    items the caller may see (hidden = missing).
  - API contract `api/openapi.yaml` with examples captured live (`api/fixtures`), checked by
    contract tests using Jellyfin's own serializer settings.
  - `scripts/e2e-test.sh`: live end-to-end checks of the read path.
- Fetch status `skipped` (no TMDB id, or unknown to TMDB).

### Fixed
- `KasuMetaDatabase.Dispose` releases only its own connection pool. The tests' process-wide
  `ClearAllPools` made a parallel test fail about once in 25 runs ("Cannot access a disposed
  object: SQLitePCL.sqlite3"); 40 consecutive runs pass now.

### Changed
- TMDB client: movies and series now request their details with
  `append_to_response=translations` (still one call per item), because TMDB's entry for the
  original language has an empty title; it is filled from `original_title` / `original_name`.
  Seasons and episodes keep plain `/translations`. Tests replay real responses recorded for the
  test media (`tests/…/Fixtures/tmdb`).

### Added
- Phase 1.3 TMDB client: movie/series/season/episode `/translations` in one call per item,
  normalized to BCP-47; v3 key or v4 token; retries 429/5xx/network with backoff and
  `Retry-After`; 4 in flight, 20 req/s; the key is never logged.
- Phase 1.2 `LocaleChain`: BCP-47 normalization and fallback (exact → base → preferred sibling),
  Chinese split by script.
- Dev server tooling: `scripts/dev-server-setup.sh` (wizard, users, libraries, scan) and
  `scripts/smoke-test.sh`; the dev compose copies the build in on start (Jellyfin writes
  `meta.json` into plugin folders, so a read-only mount fails).
- Phase 1.1 store: KasuMeta's own SQLite database at `{DataPath}/kasumeta/kasumeta.db` (WAL,
  forward-only migrations, refuses a newer schema), localized fields/images with manual locks,
  fetch state and stale query, FTS5 probe logged at startup. Uses the server's SQLite; ships none.
- Phase 0 scaffold: plugin, configuration page (locales, fallback, TMDB/TVDB keys, refresh
  interval), `GET /KasuMeta/Info` behind Jellyfin auth. Targets Jellyfin 12.x / net10.0 (built against 12.0.0, so it loads on any 12.x server; checked against 12.1.0 and 12.4.0).
