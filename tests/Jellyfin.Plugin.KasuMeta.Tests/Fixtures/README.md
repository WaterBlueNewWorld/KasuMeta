# Test fixtures

Real responses, recorded on 2026-11-17 with the dev server's TMDB Read Access Token, for the
titles in the dev server's test media. Tests replay them through a fake HTTP handler; no test
calls the network.

`Q` below is the query the client sends for the default enabled locales:
`append_to_response=translations,images&include_image_language=en,es,ja,zh`.

| File | Request |
|---|---|
| `tmdb/movie-9989.json` | `GET /3/movie/9989?Q` (Antitrust) |
| `tmdb/movie-22970.json` | `GET /3/movie/22970?Q` (The Cabin in the Woods) |
| `tmdb/tv-94523.json` | `GET /3/tv/94523?Q` (Magia Record) |
| `tmdb/tv-80834.json` | `GET /3/tv/80834?Q` (Revue Starlight) |
| `tmdb/tv-80834-s1.json` | `GET /3/tv/80834/season/1?Q` |
| `tmdb/tv-80834-s1e1.translations.json` | `GET /3/tv/80834/season/1/episode/1/translations` |
| `tmdb/movie-404.json` | `GET /3/movie/999999999?…` (404 body) |

They contain no credentials. To re-record, send the same requests with a key and replace the
files; then fix whatever assertions TMDB's edits have changed (titles, chosen poster paths).

Data © TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.

## AniList

Recorded on 2026-11-17 from `POST https://graphql.anilist.co` (no key), with the query in
`AniListClient.Query` and `{"id": N}` as variables. AniList's Cloudflare front refuses requests
without a recognisable User-Agent (error 1010), so send one when re-recording.

| File | Media id |
|---|---|
| `anilist/media-104051.json` | 104051 (Magia Record, season 1) |
| `anilist/media-98658.json` | 98658 (Revue Starlight) |
| `anilist/media-404.json` | 999999999 (HTTP 404 body) |

Data from [AniList](https://anilist.co).

## TVDB

Recorded on 2026-11-17 from `https://api4.thetvdb.com/v4/` with the dev server's TVDB key, then
trimmed to the fields `TvdbClient` reads (characters, companies, trailers and the like removed).
The tests log in with a made-up token; no fixture holds a key or token.

| File | Request |
|---|---|
| `tvdb/movie-963.json` | `GET /movies/963/extended?meta=translations` (The Cabin in the Woods) |
| `tvdb/series-370931.json` | `GET /series/370931/extended?meta=translations` (Magia Record; also answers the `short=true` season lookup) |
| `tvdb/season-1805557.json` | `GET /seasons/1805557/extended?meta=translations` (Magia Record, season 1) |
| `tvdb/episode-7489229.json` | `GET /episodes/7489229/extended?meta=translations` (Magia Record S1E1) |
| `tvdb/series-346988-episodes-s1e5.json` | `GET /series/346988/episodes/default?page=0&season=1&episodeNumber=5` (Revue Starlight) |
| `tvdb/remoteid-tt1259521.json` | `GET /search/remoteid/tt1259521` |
| `tvdb/not-found.json` | `GET /series/99999999/extended` (HTTP 404 body) |

Metadata provided by TheTVDB (https://thetvdb.com).
