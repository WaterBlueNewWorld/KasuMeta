# Changelog

All notable changes to KasuMeta. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/); the plugin's four-part version is
the release's three parts plus `.0` (release 1.0.0 is plugin version 1.0.0.0).

## [Unreleased]

## [1.1.0] - 2026-11-18

Requires **Jellyfin 12.x** (built against 12.0, tested on 12.0, 12.1 and 12.4) and a TMDB or a
TVDB API key. Existing installs keep working as before: TMDB stays the only provider until TVDB
is turned on.

### Added
- **TVDB as a metadata provider:** titles, overviews, taglines and localized posters, logos and
  backdrops from TheTVDB (API v4; needs a TVDB key, plus the subscriber PIN for user-supported
  keys). Items are found by their TVDB id; movies and series without one by their IMDb id,
  seasons and episodes by their series' TVDB id and numbers.
- **Choice of providers:** TMDB only (the default, as before), TVDB only, or both. With both, a
  **priority weight** per provider (0–100) decides, per field, image type and locale, whose value
  is kept; the other fills what it lacks, and AniList still fills only what both left empty.
  Changing the choice, or which provider wins, fetches the library again.
- The Status panel and `GET /KasuMeta/Status` show the providers in use, in priority order
  (`MetadataProviders`), and whether a TVDB key is set (`TvdbConfigured`).

## [1.0.0] - 2026-11-18

First release. Requires **Jellyfin 12.x** (built against 12.0, tested on 12.0, 12.1 and 12.4)
and a **TMDB API key**.

### Added
- **Localized titles, overviews and taglines** per locale from TMDB, one request per item
  whatever the number of languages. Kept beside Jellyfin in the plugin's own SQLite database
  (`<data>/kasumeta/kasumeta.db`); Jellyfin's own database and metadata are never written to.
- **Localized artwork:** posters, logos and backdrops per language and region. Raster images
  are preferred over SVG.
- **Fallback per field and per image type** (`es-MX` → `es` → `es-ES`), when "Fall back to the
  base language" is on. Scripts never stand in for each other: Traditional and Simplified
  Chinese stay apart, and so do romanized titles (`ja-Latn`) and Japanese script.
- **Search by localized name** in every stored language. Case and accents are ignored, the last
  word matches as a prefix, and Chinese, Japanese, Korean and Thai text matches anywhere in a
  title. Uses SQLite's FTS5 when the server has it, substring matching otherwise.
- **Anime titles from AniList** for movies and series with an AniList id (no key needed; can be
  turned off). AniList fills what TMDB lacks; romaji titles are always stored, so search finds
  them from any language, and they're shown to clients that ask for `ja-Latn`.
- **Manual overrides** for administrators: set, lock or remove titles, overviews, taglines,
  sort names and images per locale. Locked values survive provider refreshes; a removed value
  comes back from the provider.
- **Library-aware:** new and changed items are fetched within seconds, removed items are
  forgotten, a re-identified item drops the old title's translations, and users only see or find
  items their library access allows.
- **Scheduled tasks** under Dashboard › Scheduled Tasks › KasuMeta: "Fetch missing
  translations" (at startup and daily) and "Refresh stale translations" (daily; also retries
  failures).
- **Status panel** on the plugin page: fetch progress, recent failures with the items' names,
  and each provider's requests, retries and failures since the server started. Provider
  failures are also logged as warnings.
- **HTTP API** under `/KasuMeta`, behind Jellyfin's own authentication: `Info`, `Items/Batch`,
  `Items/{id}`, `Search`, and the admin routes `Status`, `Items/{id}/All`,
  `PUT Items/{id}/{locale}` and `Items/{id}/Refresh`. Contract: `api/openapi.yaml`, with
  example responses captured from a live server.
- **Polite to providers:** TMDB at most 20 requests a second, AniList at most 30 a minute,
  with retries that honour `Retry-After`. API keys stay on the server and are never logged.

### Notes
- After installing and setting the TMDB key, the first backfill fetches the whole library: one
  TMDB request per item (about 40 minutes for 50,000 items), plus one AniList request per
  anime with an AniList id.
- AniList ids come from Jellyfin's AniList metadata plugin, or can be set by hand under an
  item's External IDs.
