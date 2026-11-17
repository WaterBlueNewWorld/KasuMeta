# Changelog

All notable changes to KasuMeta. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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
