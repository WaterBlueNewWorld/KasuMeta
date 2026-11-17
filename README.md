# KasuMeta

A Jellyfin plugin that serves localized titles, overviews and artwork to
client apps, so each user sees metadata in their own language.
It keeps its own SQLite store beside Jellyfin and never writes to Jellyfin's database.

## Compatibility

| Component | Jellyfin server |
|---|---|
| **the client app** (the app) | **10.11.x or higher** |
| **KasuMeta** (this plugin: multi-locale metadata) | **12.x only** (production runs 12.1) |

- **KasuMeta is optional.** On a server without it, the client app works as before: it shows
  metadata in the server's language, the way stock Jellyfin does.
- **Server versions:** on 10.11 servers the plugin can't be installed, because Jellyfin 12
  moved to .NET 10.
- **Build:** the plugin is built against Jellyfin **12.0.0** (`targetAbi 12.0.0.0`), so one DLL
  loads on any 12.x server. It has been checked against 12.1.0 and 12.4.0.

## Why a server plugin

the client app avoids changes to the Jellyfin backend, and has turned down a server plugin before
(for user preferences and the colour scheme). This is a deliberate exception:
- Per-user, multi-locale metadata needs shared storage on the server.
- Provider API keys must never ship in the app.

A plugin is the safest way to meet both:
- It leaves Jellyfin's own database and metadata fields untouched.
- It can be removed at any time.
- Without it, the app falls back to stock data.

## Build

Requires the .NET 10 SDK.

```sh
dotnet test                                  # build + unit tests (against Jellyfin 12.0.0)
dotnet test -p:JellyfinVersion=12.1.0        # check against another 12.x line
dotnet build -c Release                      # → src/Jellyfin.Plugin.KasuMeta/bin/Release/net10.0/Jellyfin.Plugin.KasuMeta.dll
```

To install by hand, copy the DLL to `<jellyfin config>/plugins/KasuMeta_0.0.1.0/` and restart
the server.

To run a local 12.1 dev server with the plugin installed, use port 8097. It runs separately from
the client's 10.11 dev server.

```sh
dotnet build -c Release
docker compose -f docker/dev/compose.yaml up -d
bash scripts/dev-server-setup.sh     # first run: wizard, users, libraries; writes .env (git-ignored)
bash scripts/smoke-test.sh           # live checks: plugin loaded, database, auth
bash scripts/e2e-test.sh             # live read-path checks (needs a TMDB key on the plugin page)
docker restart kasumeta-jellyfin     # after each rebuild
```

## API

Three routes, all behind Jellyfin's own sign-in. JSON is PascalCase, as Jellyfin serializes, and
null fields are left out. The contract is [`api/openapi.yaml`](api/openapi.yaml), with examples
captured from a live server in [`api/fixtures`](api/fixtures).

| Route | Use |
|---|---|
| `GET /KasuMeta/Info` | Capability check after sign-in (404 = plugin not installed) |
| `POST /KasuMeta/Items/Batch` | Localized overlays for up to 200 items (one call per grid page) |
| `GET /KasuMeta/Items/{itemId}?locale=es-MX` | One item's overlay (detail page) |

## Status

- **Done:**
  - Phase 0: `GET /KasuMeta/Info` and the configuration page work.
  - Phase 1, the read path: store, locale fallback, TMDB client, fetch queue and worker,
    library hooks, scheduled tasks, and the batch and item endpoints.
  - Verified live on Jellyfin 12.1.
- **Next:** Phase 2, localized images (posters and logos per locale).
- **Not supported yet:** Jellyfin 10.11. It's kept as a future option.
