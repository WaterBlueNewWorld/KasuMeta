# Test fixtures

Real responses, recorded on 2026-11-17 with the dev server's TMDB Read Access Token, for the
titles in the client's `test-media/`. Tests replay them through a fake HTTP handler; no test
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
