# KasuMeta

A Jellyfin plugin that serves localized titles, overviews, taglines and artwork, so each user
of a client app can see metadata in their own language instead of the one the library was
scanned in.

> [!IMPORTANT]
> **KasuMeta has no effect on Jellyfin's stock UI.** It's made for other frontends to consume:
> the localized metadata is only served through KasuMeta's own API (`/KasuMeta/...`), which a
> client app must call itself, as KasuVideo does. Jellyfin's web client and the official apps
> keep showing the library's own metadata, with or without the plugin installed.

Jellyfin stores one set of metadata per item, in the server's language. KasuMeta keeps
translations beside it, in its own SQLite file, and serves them through its own API. It never
writes to Jellyfin's database or metadata fields, so stock clients behave exactly as before, and
removing the plugin leaves the server as it was.

## Features

- **Titles, overviews, taglines, genres and tags per locale** from TMDB, TVDB and AniList,
  fetched once per item whatever the number of languages, and kept up to date as the library
  changes.
- **Sources per field:** choose, for titles, overviews, taglines, genres, tags and each image
  type, which providers they come from and in what order. For each language the first provider
  with a value wins and the next ones fill what it lacks: a title TVDB doesn't have in Spanish
  comes from TMDB, while posters can come from TMDB only.
- **Original-language fallback:** with nothing in the reader's language, a field is shown in
  the title's original language (a Japanese series' Japanese synopsis); with nothing at all, it
  stays empty.
- **Translated genres and tags:** TMDB's own genre names per language, and an admin dictionary
  that translates every other (English) genre and tag for the whole library at once.
- **Localized artwork:** posters, logos and backdrops per language and region (a Mexican poster
  is not a Spanish one).
- **Fallback per field:** `es-MX` falls back to `es-ES` when TMDB has a Spanish overview but no
  Mexican one. Scripts never stand in for each other: Traditional and Simplified Chinese stay
  apart, and so do romaji (`ja-Latn`) and Japanese script.
- **Search by localized name**, in any stored language: "cabana del terror" or "キャビン" finds
  *The Cabin in the Woods*. Case and accents are ignored, and Chinese, Japanese, Korean and Thai
  text matches anywhere in a title.
- **Anime titles from AniList** for items with an AniList id: English, native and romaji titles
  where TMDB and TVDB have none. Romaji is always searchable.
- **Manual overrides:** administrators can set and lock titles, overviews, genres, tags and
  images per locale, which provider refreshes never replace. An image can be the provider's URL,
  any other URL, or a file uploaded to the server (up to 10 MB).
- **Respects library access:** users only ever see, or find, items they're allowed to see.

## Compatibility

| | |
|---|---|
| Jellyfin | **12.x** (built against 12.0, tested on 12.0, 12.1 and 12.4) |
| .NET | 10, as Jellyfin 12 |

Jellyfin 10.11 and older can't load the plugin: Jellyfin 12 moved to .NET 10.

## Installation

**From the plugin repository** (once the first release is published):

1. Dashboard › Plugins › Repositories › add
   `https://waterbluenewworld.github.io/KasuMeta/manifest.json`.
2. Install KasuMeta from the Catalog and restart Jellyfin.

**By hand:**

1. Build the plugin (see [Building](#building)), or take the zip from a release.
2. Copy `Jellyfin.Plugin.KasuMeta.dll` into `<jellyfin config>/plugins/KasuMeta_1.2.0.0/`.
3. Restart Jellyfin. KasuMeta appears under Dashboard › Plugins.

## Configuration

Dashboard › Plugins › KasuMeta:

| Setting | |
|---|---|
| Enabled locales | BCP-47 codes, e.g. `en-US, es-MX, ja-JP`. Translations are kept for these locales and their fallbacks. |
| Fall back to the base language | Lets `es-MX` be answered with `es` or `es-ES`. |
| Use the original language when nothing else exists | On by default. A field with nothing in the reader's language (nor, with the setting above, another region of it) is served in the item's original language. Applies even with base-language fallback off. |
| Sources per field | For titles, overviews, taglines, genres, tags, posters, logos and backdrops: up to three providers, first choice first. For each language the first one with a value wins; the next ones fill what it lacks. A provider is used only when some field lists it and it is set up. |
| TMDB API key | A v3 key or a v4 read access token. Needed when TMDB is listed. It stays on the server; clients never see it. |
| TVDB API key, subscriber PIN | A TVDB v4 key, needed when TVDB is listed; the PIN only for a user-supported key. Both stay on the server. |
| Refresh interval | How old a translation may get before it's fetched again. |

Until the page is saved, an install upgraded from 1.1 keeps its old choice: TMDB and TVDB as
they were turned on, ordered by their weights, then AniList. AniList needs no key; items need an
AniList id, as set by Jellyfin's AniList metadata plugin or by hand under the item's External IDs.

After the key is set, the "Fetch missing translations" task fetches the whole library: one
TMDB request per item, at most 20 a second. AniList allows 30 requests a minute, so anime
take longer.

**TVDB** is asked by the item's TVDB id. Movies and series without one are looked up by their
IMDb id, and seasons and episodes without one by their series' TVDB id and their numbers (in
the series' default episode order), each lookup one request more; at most 10 requests a second.
TVDB has one translation per language rather than per country, so its Spanish is stored for
every enabled Spanish locale (`es-MX`, `es-ES`): where TVDB wins, both show the same text.

Changing which providers a field comes from, or setting the key of a listed provider, re-queues
the library: each item is fetched again from its providers (one request per item and provider).

**Genres and tags.** TMDB names its genres in many languages (two cached genre lists per
language a day). Every other genre and tag (TMDB's keywords, TVDB's and AniList's genres and
tags) is English. Those are kept as the item's English terms, and the **Genre and tag
dictionary** on the same page translates them for the whole library: pick genres or tags and a
language, and type the translations. They're served at once, without a re-fetch; terms with no
translation stay in English. TMDB leaves a few genres in English even in its other languages
("Sci-Fi & Fantasy", "Action & Adventure"); those are listed among the English terms too, and a
dictionary entry for them replaces them in TMDB's lists.

**Original language.** Each item's original language comes from Jellyfin (for seasons and
episodes, their series'), else from TMDB or TVDB. Its values are kept even when that language
isn't enabled, so they can be served as the last resort.

The **Status** panel on the same page shows the progress (items waiting, fetched, failed), the
latest failures with the items' names, and each provider's requests, retries and failures since
the server started. Failed items are retried by the daily "Refresh stale translations" task, and
each failure is logged as a warning.

## API

All routes sit behind Jellyfin's own authentication: send the token your client already uses.
JSON is PascalCase, as everywhere in Jellyfin, and empty fields are left out. The contract is
[`api/openapi.yaml`](api/openapi.yaml), with example responses captured from a live server in
[`api/fixtures`](api/fixtures).

| Route | Use |
|---|---|
| `GET /KasuMeta/Info` | Capability check: a 404 means the plugin isn't installed |
| `POST /KasuMeta/Items/Batch` | Titles, overviews, taglines, genres, tags and images for up to 200 items (one call per grid page) |
| `GET /KasuMeta/Items/{itemId}?locale=es-MX` | One item's localized data (detail page) |
| `GET /KasuMeta/Search?q=cabana&locale=es-MX` | Find items by their localized names |
| `GET /KasuMeta/Status` | Admin: fetch progress, recent failures and provider counters |
| `GET /KasuMeta/Items/{itemId}/All` | Admin: every stored locale of an item, with sources, locks and fetch state |
| `PUT /KasuMeta/Items/{itemId}/{locale}` | Admin: set (and lock) or remove manual values |
| `POST /KasuMeta/Items/{itemId}/Refresh` | Admin: re-fetch an item from its providers |
| `PUT /KasuMeta/Items/{itemId}/{locale}/Images/{type}` | Admin: upload an image file (multipart `file`, JPEG/PNG/WebP/GIF, at most 10 MB) |
| `GET /KasuMeta/Items/{itemId}/{locale}/Images/{type}` | An uploaded image file (overlays point here when `Uploaded` is true) |
| `GET /KasuMeta/Terms?kind=genre&locale=es` | Admin: the genre or tag dictionary for a language, with the terms in use |
| `PUT /KasuMeta/Terms/{kind}/{locale}` | Admin: set or remove dictionary translations |

How a client uses it:
1. Call `Info` after sign-in. If it's a 404, keep to Jellyfin's own fields. `Info.Features`
   (`text`, `images`, `search`, `overrides`, `genres`, `tags`, `uploads`, `terms`,
   `original-language`) says what this build serves.
2. Load items from Jellyfin as usual, then ask `Items/Batch` for the same ids in the user's
   locale, and show the localized values where there are some. `ResolvedLocale` says which
   locale each value really came from.
3. Search with `Search` beside Jellyfin's own search, and fetch the returned ids from Jellyfin.

The admin routes need an administrator's token or an API key. A manual value is locked by
default. Genres and tags are sent as `Values` lists. Sending `null` for a field or image removes
it (an uploaded file too), and the provider's value comes back on the re-fetch that follows. An
uploaded image's `Url` is relative to the server's base URL and is fetched with the user's token.

## Building

Requires the .NET 10 SDK.

```sh
dotnet test                                  # build + unit tests (against Jellyfin 12.0.0)
dotnet test -p:JellyfinVersion=12.1.0        # check against another 12.x release
dotnet build -c Release                      # → src/Jellyfin.Plugin.KasuMeta/bin/Release/net10.0/Jellyfin.Plugin.KasuMeta.dll
```

### Local dev server

[`docker/dev/compose.yaml`](docker/dev/compose.yaml) runs a Jellyfin 12.1 server on port 8097
with the Release build installed, and the scripts drive it:

```sh
dotnet build -c Release
docker compose -f docker/dev/compose.yaml up -d
bash scripts/dev-server-setup.sh     # first run: wizard, users, libraries; writes .env (git-ignored)
bash scripts/smoke-test.sh           # live checks: plugin loaded, database, auth
bash scripts/dev-anilist-setup.sh    # once, after the scan: AniList ids for the anime test series
bash scripts/e2e-test.sh             # live end-to-end checks (needs a TMDB key on the plugin page)
docker restart kasumeta-jellyfin     # after each rebuild
```

### Continuous integration and releases

Every push and pull request runs [CI](.github/workflows/ci.yml): the version check, the tests
against Jellyfin 12.0.0, 12.1.0 and the newest 12.x on NuGet, and the plugin zip (kept as a
build artifact).

Releases are cut by pushing a version tag. [RELEASING.md](RELEASING.md) has the full procedure,
step by step, with the commands. In short, for example for 0.1.0:

1. Set the version in `Directory.Build.props` (`0.1.0.0`), `build.yaml` (with a new
   `changelog`), the plugin folder in `docker/dev/compose.yaml` and the install path in this
   README. `bash scripts/check-version.sh` confirms they agree.
2. Rename `[Unreleased]` in `CHANGELOG.md` to `[0.1.0]`; it becomes the release notes.
3. Push the tag `v0.1.0`. The [release workflow](.github/workflows/release.yml) runs CI,
   publishes a GitHub release with the zip, and adds the version to `manifest.json` on the
   `gh-pages` branch.

After the first release, enable GitHub Pages once: Settings › Pages › Deploy from a branch ›
`gh-pages`, `/ (root)`. The first release workflow creates that branch, and GitHub only offers
existing branches.

## Status

1.2.0: providers chosen per field, the original-language fallback, genres and tags with a
translation dictionary, and image uploads, on top of 1.1.0's TMDB and TVDB providers and 1.0.0's
localized text, images, search, manual overrides, AniList titles and status panel. Verified on a
live Jellyfin 12.1 server. See [CHANGELOG.md](CHANGELOG.md).

## Attribution

This product uses the TMDB API but is not endorsed or certified by TMDB. Metadata provided by
[TheTVDB](https://thetvdb.com), when TVDB is turned on. Anime titles come from
[AniList](https://anilist.co).

## License

[GPL-3.0](LICENSE), like Jellyfin and its official plugins.
