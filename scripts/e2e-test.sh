#!/usr/bin/env bash
# End-to-end checks of the read path against the dev server, after dev-server-setup.sh and with
# a TMDB key set on the plugin page. Uses the test media's real titles, so it depends on what TMDB
# returns for them; if TMDB edits a translation, update the expected strings below.
#
#   bash scripts/e2e-test.sh
#
# Temporarily restricts the test user to the Movies library (visibility check) and restores it.
# With a TVDB key on the plugin page, also switches titles to "TVDB, then TMDB" for a moment (the
# library is fetched again, twice) and puts the saved configuration back. Uploads a logo and adds
# a dictionary entry, and removes both afterwards.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
while IFS='=' read -r key value; do
  [[ -z "$key" || "$key" == \#* ]] && continue
  export "$key=${value%$'\r'}"
done <"$repo_root/.env"

base="${JELLYFIN_URL%/}"
client='MediaBrowser Client="KasuMeta e2e", Device="e2e-test.sh", DeviceId="kasumeta-e2e", Version="0.0.0"'
pass() { printf '  ok   %s\n' "$*"; }
fail() { printf '  FAIL %s\n' "$*" >&2; exit 1; }
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
# json EXPR: evaluates a Python expression over the JSON on stdin (d = parsed body).
json() { python -I -c "import sys,json; sys.stdout.reconfigure(encoding='utf-8'); d=json.load(sys.stdin); print($1)"; }

login() {
  curl -sS --fail-with-body -X POST -H "Authorization: $client" -H 'Content-Type: application/json' \
    --data "{\"Username\":\"$1\",\"Pw\":\"$2\"}" "$base/Users/AuthenticateByName"
}
admin_login="$(login "$JELLYFIN_ADMIN_USER" "$JELLYFIN_ADMIN_PASSWORD")"
user_login="$(login "$JELLYFIN_TEST_USER" "$JELLYFIN_TEST_PASSWORD")"
A="Authorization: $client, Token=\"$(json "d['AccessToken']" <<<"$admin_login")\""
U="Authorization: $client, Token=\"$(json "d['AccessToken']" <<<"$user_login")\""
user_id="$(json "d['User']['Id']" <<<"$user_login")"

# Item ids by name, as the test user sees them.
items="$(curl -sS -H "$U" "$base/Items?Recursive=true&IncludeItemTypes=Movie,Series")"
id_of() { json "[i['Id'] for i in d['Items'] if i['Name']=='$1'][0]" <<<"$items"; }
antitrust="$(id_of Antitrust)"
cabin="$(id_of 'The Cabin in the Woods')"
magia="$(id_of 'Magia Record: Puella Magi Madoka Magica Side Story')"
twin="$(id_of 'Twin Check (2026)')"

batch() {
  curl -sS -X POST -H "$U" -H 'Content-Type: application/json' --data "$1" "$base/KasuMeta/Items/Batch"
}

echo "Read path"
body="$(batch "{\"ItemIds\":[\"$antitrust\",\"$cabin\",\"$magia\",\"$twin\"],\"Locale\":\"es_mx\"}")"
[[ "$(json "d['Locale']" <<<"$body")" == "es-MX" ]] && pass "locale normalized to es-MX" || fail "locale: $body"
[[ "$(json "d['Items']['$antitrust']['Name']['Value']" <<<"$body")" == "Amenaza Virtual" ]] \
  && pass "Antitrust es-MX title" || fail "Antitrust title"
[[ "$(json "d['Items']['$cabin']['Name']['Value']" <<<"$body")" == "La cabaña del terror" ]] \
  && pass "Cabin in the Woods es-MX title (differs from Spain)" || fail "Cabin title"
[[ "$(json "d['Items']['$magia']['Overview']['ResolvedLocale']" <<<"$body")" == "es-ES" ]] \
  && pass "Magia Record overview falls back to es-ES (no es-MX overview)" || fail "per-field fallback"
[[ "$(json "'$twin' in d['Missing']" <<<"$body")" == "True" ]] && pass "item without a TMDB id is Missing" || fail "Missing"

item="$(curl -sS -H "$U" "$base/KasuMeta/Items/$antitrust?locale=en-US")"
[[ "$(json "d['Name']['Value']" <<<"$item")" == "Antitrust" ]] \
  && pass "en-US title filled from original_title" || fail "original title: $item"

echo "Images"
body="$(batch "{\"ItemIds\":[\"$antitrust\",\"$cabin\"],\"Locale\":\"es-MX\",\"Fields\":[],\"Images\":[\"Primary\",\"Logo\"]}")"
cabin_poster="$(json "d['Items']['$cabin']['Images']['Primary']['Url']" <<<"$body")"
[[ "$cabin_poster" == https://image.tmdb.org/t/p/original/* && "$(json "d['Items']['$cabin']['Images']['Primary']['ResolvedLocale']" <<<"$body")" == "es-MX" ]] \
  && pass "Cabin in the Woods has a Mexican poster" || fail "Cabin poster: $body"
[[ "$(json "d['Items']['$antitrust']['Images']['Primary']['ResolvedLocale']" <<<"$body")" == "es-ES" ]] \
  && pass "Antitrust's poster falls back to es-ES (no Mexican poster)" || fail "Antitrust poster fallback"
[[ "$(json "'Name' in d['Items']['$cabin']" <<<"$body")" == "False" ]] && pass "\"Fields\": [] leaves the text out" || fail "fields filter"
[[ "$(json "d['Items']['$cabin']['Images']['Primary']['Width'] > 0" <<<"$body")" == "True" ]] && pass "image sizes served" || fail "sizes"
[[ "$(json "all(not i['Url'].lower().endswith('.svg') for it in d['Items'].values() for i in it['Images'].values())" <<<"$body")" == "True" ]] \
  && pass "no SVG served" || fail "svg"
cors="$(curl -sI -H 'Origin: https://client.example' "${cabin_poster/\/original\//\/w342\/}" | tr -d '\r' | grep -i '^access-control-allow-origin:' || true)"
[[ -n "$cors" ]] && pass "TMDB serves the poster (w342) with CORS for the web app" || fail "no CORS header on the image"
body="$(batch "{\"ItemIds\":[\"$cabin\"],\"Locale\":\"es-MX\",\"Images\":[]}")"
[[ "$(json "'Images' in d['Items']['$cabin']" <<<"$body")" == "False" ]] && pass "\"Images\": [] leaves images out" || fail "images filter"
[[ "$(status -X POST -H "$U" -H 'Content-Type: application/json' --data "{\"ItemIds\":[\"$cabin\"],\"Locale\":\"es-MX\",\"Images\":[\"Poster\"]}" "$base/KasuMeta/Items/Batch")" == 400 ]] \
  && pass "unknown image type -> 400" || fail "400 image type"

echo "Search"
# search Q LOCALE [EXTRA]: the query is percent-encoded as UTF-8 by Python, because curl on
# Windows would send non-ASCII arguments in the ANSI code page.
search() {
  local q
  q="$(python -I -c "import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1]))" "$1")"
  curl -sS -H "$U" "$base/KasuMeta/Search?q=$q&locale=$2${3:+&$3}"
}
[[ "$(curl -sS -H "$U" "$base/KasuMeta/Info" | json "'search' in d['Features']")" == "True" ]] && pass "Info lists the search feature" || fail "features"
body="$(search "cabana del terr" es-MX)"
[[ "$(json "(d['Results'][0]['ItemId'], d['Results'][0]['MatchedName']) == ('$cabin', 'La cabaña del terror')" <<<"$body")" == "True" ]] \
  && pass "accents and case ignored, last word a prefix (cabana del terr)" || fail "search cabana: $body"
body="$(search "キャビン" es-MX)"
[[ "$(json "(d['Results'][0]['ItemId'], d['Results'][0]['MatchedLocale']) == ('$cabin', 'ja-JP')" <<<"$body")" == "True" ]] \
  && pass "found from a Spanish session by its Japanese title" || fail "search ja: $body"
body="$(search "まどか" ja-JP)"
[[ "$(json "d['Results'][0]['ItemId']" <<<"$body")" == "$magia" ]] && pass "Japanese text found in the middle of a title" || fail "search mid-title: $body"
body="$(search "chicas del esc" es-MX)"
[[ "$(json "d['Results'][0]['Type']" <<<"$body")" == "Episode" ]] && pass "episodes are searchable" || fail "search episode: $body"
body="$(search "revue starlight" en-US)"
[[ "$(json "d['Results'][0]['Type']" <<<"$body")" == "Series" ]] && pass "a series ranks above the episode named after it" || fail "kind rank: $body"
body="$(search "revue" en-US "types=Series")"
[[ "$(json "sorted({r['Type'] for r in d['Results']})" <<<"$body")" == "['Series']" ]] && pass "types filter" || fail "types: $body"
body="$(search "the" en-US "limit=1")"
[[ "$(json "len(d['Results'])" <<<"$body")" == 1 ]] && pass "limit" || fail "limit: $body"
[[ "$(status -H "$U" "$base/KasuMeta/Search?q=%21%21&locale=es-MX")" == 400 ]] && pass "nothing searchable -> 400" || fail "400 search"
[[ "$(status "$base/KasuMeta/Search?q=cabin&locale=es-MX")" == 401 ]] && pass "search without a token -> 401" || fail "401 search"

echo "AniList"
magia_ids="$(curl -sS -H "$A" "$base/Items?Recursive=true&IncludeItemTypes=Series&Fields=ProviderIds" | json "[i['ProviderIds'] for i in d['Items'] if i['Id']=='$magia'][0]")"
if [[ "$magia_ids" != *AniList* ]]; then
  printf '  skip AniList checks: run scripts/dev-anilist-setup.sh once to set the AniList ids\n'
else
  body="$(curl -sS -H "$A" "$base/KasuMeta/Items/$magia/All")"
  [[ "$(json "(d['Fetch']['Status'], d['Locales']['ja-Latn']['Name']['Source'], d['Locales']['en-US']['Name']['Source'])" <<<"$body")" == "('ok', 'anilist', 'tmdb')" ]] \
    && pass "romaji from AniList; TMDB's English title kept (TMDB first)" || fail "anilist sources: $body"
  romaji_on="$(curl -sS -H "$U" "$base/KasuMeta/Info" | json "'ja-Latn' in d['EnabledLocales']")"
  body="$(search "mahou shoujo" en_US)"
  [[ "$(json "(d['Results'][0]['ItemId'], d['Results'][0]['MatchedLocale'])" <<<"$body")" == "('$magia', 'ja-Latn')" ]] \
    && pass "an en-US user finds it by its romaji title (ja-Latn enabled: $romaji_on)" || fail "romaji search: $body"
  [[ "$(batch "{\"ItemIds\":[\"$magia\"],\"Locale\":\"en-US\",\"Fields\":[\"Name\"]}" | json "d['Items']['$magia']['Name']['ResolvedLocale']")" == "en-US" ]] \
    && pass "and is still shown the English title" || fail "en-US title"
  body="$(batch "{\"ItemIds\":[\"$magia\",\"$antitrust\"],\"Locale\":\"ja-Latn\",\"Fields\":[\"Name\"]}")"
  [[ "$(json "d['Items']['$magia']['Name']['Value']" <<<"$body")" == "Magia Record: Mahou Shoujo Madoka☆Magica Gaiden" ]] \
    && pass "ja-Latn serves the romaji title" || fail "romaji: $body"
  # Nothing in ja-Latn or its group: the original language (English) stands in, never kanji.
  [[ "$(json "d['Missing'] == [] and d['Items']['$antitrust']['Name']['ResolvedLocale'] == 'en-US'" <<<"$body")" == "True" ]] \
    && pass "a title without romaji falls back to its original language (English), not to kanji" || fail "ja-Latn fell back: $body"
  [[ "$(curl -sS -H "$U" "$base/KasuMeta/Items/$magia?locale=ja-JP" | json "d['Name']['ResolvedLocale']")" == "ja-JP" ]] \
    && pass "ja-JP still gets the Japanese-script title" || fail "ja-JP"
fi

echo "Overrides"
all() { curl -sS -H "$A" "$base/KasuMeta/Items/$cabin/All"; }
put_es_mx() { curl -sS -X PUT -H "$A" -H 'Content-Type: application/json' --data "$1" "$base/KasuMeta/Items/$cabin/es-MX"; }
# wait_all EXPR: polls the admin view until the Python expression over it is True (20 s at most).
wait_all() {
  local i
  for i in $(seq 1 40); do
    [[ "$(all | json "$1")" == "True" ]] && return 0
    sleep 0.5
  done
  return 1
}
[[ "$(curl -sS -H "$U" "$base/KasuMeta/Info" | json "'overrides' in d['Features']")" == "True" ]] && pass "Info lists the overrides feature" || fail "features"
[[ "$(status -H "$U" "$base/KasuMeta/Items/$cabin/All")" == 403 ]] \
  && [[ "$(status -X POST -H "$U" "$base/KasuMeta/Items/$cabin/Refresh")" == 403 ]] \
  && [[ "$(status -X PUT -H "$U" -H 'Content-Type: application/json' --data '{"Fields":{"Name":{"Value":"x"}}}' "$base/KasuMeta/Items/$cabin/es-MX")" == 403 ]] \
  && pass "admin routes -> 403 for a non-admin (RequiresElevation exists on this server)" || fail "403 admin routes"
body="$(all)"
[[ "$(json "{'es-ES', 'es-MX', 'ja-JP'} <= set(d['Locales']) and d['Locales']['es-ES']['Name']['ResolvedLocale'] == 'es-ES' and d['Fetch']['Status'] == 'ok'" <<<"$body")" == "True" ]] \
  && pass "admin view lists every stored locale, unresolved, with the fetch state" || fail "all: $body"
trap 'put_es_mx "{\"Fields\":{\"Name\":null}}" >/dev/null' EXIT
body="$(put_es_mx '{"Fields":{"Name":{"Value":"Refugio maldito"}}}')"
[[ "$(json "d['Locales']['es-MX']['Name'] == {'Value': 'Refugio maldito', 'Source': 'manual', 'ResolvedLocale': 'es-MX', 'Locked': True}" <<<"$body")" == "True" ]] \
  && pass "PUT sets a locked manual title" || fail "put: $body"
[[ "$(batch "{\"ItemIds\":[\"$cabin\"],\"Locale\":\"es-MX\"}" | json "d['Items']['$cabin']['Name']['Value']")" == "Refugio maldito" ]] \
  && pass "the read path serves it to a normal user" || fail "manual title not served"
[[ "$(search "refugio" es-MX | json "d['Results'][0]['ItemId']")" == "$cabin" ]] && pass "and search finds it at once" || fail "manual title not searchable"
before="$(all | json "d['Fetch']['LastFetch']")"
[[ "$(status -X POST -H "$A" "$base/KasuMeta/Items/$cabin/Refresh")" == 202 ]] && pass "Refresh -> 202" || fail "refresh"
wait_all "d['Fetch']['LastFetch'] != '$before'" && pass "the refresh ran" || fail "refresh never ran"
[[ "$(all | json "d['Locales']['es-MX']['Name']['Value']")" == "Refugio maldito" ]] && pass "and left the locked title alone" || fail "lock lost"
put_es_mx '{"Fields":{"Name":null}}' >/dev/null
trap - EXIT
wait_all "d['Locales']['es-MX'].get('Name', {}).get('Value') == 'La cabaña del terror'" \
  && pass "null removes it, and the re-fetch brings TMDB's title back" || fail "revert"
[[ "$(search "refugio" es-MX | json "len(d['Results'])")" == 0 ]] && pass "the removed title is no longer found" || fail "stale search entry"
[[ "$(status -X PUT -H "$A" -H 'Content-Type: application/json' --data '{"Images":{"Primary":{"Url":"file:///etc/passwd"}}}' "$base/KasuMeta/Items/$cabin/es-MX")" == 400 ]] \
  && pass "a non-http image URL -> 400" || fail "400 url"
[[ "$(status -X PUT -H "$A" -H 'Content-Type: application/json' --data '{"Fields":{"Name":{"Value":"x"}}}' "$base/KasuMeta/Items/00000000000000000000000000000001/es-MX")" == 404 ]] \
  && pass "an unknown item -> 404" || fail "404 unknown item"

echo "Uploads"
tmp_dir="$(mktemp -d)"
# A 64x48 PNG header padded to 2 KB (KasuMeta reads the size from the IHDR), and an 11 MB one.
python -I -c "
import struct, sys
head = b'\x89PNG\r\n\x1a\n' + struct.pack('>I', 13) + b'IHDR' + struct.pack('>II', 64, 48)
open(sys.argv[1], 'wb').write(head + bytes(2048 - len(head)))
open(sys.argv[2], 'wb').write(head + bytes(11 * 1024 * 1024))
open(sys.argv[3], 'wb').write(b'<svg xmlns=\"http://www.w3.org/2000/svg\"/>')
" "$tmp_dir/logo.png" "$tmp_dir/big.png" "$tmp_dir/logo.svg"
upload() { curl -sS -o "$tmp_dir/out" -w '%{http_code}' -X PUT -H "$A" -F "file=@$1" ${2:+-F "locked=$2"} "$base/KasuMeta/Items/$cabin/es-MX/Images/Logo"; }
trap 'put_es_mx "{\"Images\":{\"Logo\":null}}" >/dev/null; rm -rf "$tmp_dir"' EXIT
[[ "$(curl -sS -H "$U" "$base/KasuMeta/Info" | json "'uploads' in d['Features']")" == "True" ]] && pass "Info lists the uploads feature" || fail "features"
[[ "$(upload "$tmp_dir/logo.png")" == 200 ]] && pass "PUT …/Images/Logo stores a PNG" || fail "upload: $(cat "$tmp_dir/out")"
logo="$(batch "{\"ItemIds\":[\"$cabin\"],\"Locale\":\"es-MX\",\"Fields\":[],\"Images\":[\"Logo\"]}" | json "json.dumps(d['Items']['$cabin']['Images']['Logo'])")"
[[ "$(json "d['Uploaded'] and (d['Width'], d['Height'], d['Source'], d['Locked']) == (64, 48, 'manual', True)" <<<"$logo")" == "True" ]] \
  && pass "the overlay points at the upload, with its size" || fail "uploaded overlay: $logo"
logo_url="$(json "d['Url']" <<<"$logo")"
[[ "$(curl -sS -o "$tmp_dir/served" -w '%{http_code} %{content_type}' -H "$U" "$base/$logo_url")" == "200 image/png" ]] \
  && cmp -s "$tmp_dir/served" "$tmp_dir/logo.png" && pass "a normal user gets the same bytes back as image/png" || fail "served upload"
[[ "$(status "$base/$logo_url")" == 401 ]] && pass "and nobody without a token" || fail "401 upload"
[[ "$(upload "$tmp_dir/big.png")" == 413 ]] && pass "11 MB -> 413" || fail "413: $(cat "$tmp_dir/out")"
[[ "$(upload "$tmp_dir/logo.svg")" == 400 ]] && pass "an SVG -> 400" || fail "svg upload"
[[ "$(status -X PUT -H "$U" -F "file=@$tmp_dir/logo.png" "$base/KasuMeta/Items/$cabin/es-MX/Images/Logo")" == 403 ]] \
  && pass "a non-admin can't upload" || fail "403 upload"
put_es_mx '{"Images":{"Logo":null}}' >/dev/null
[[ "$(status -H "$U" "$base/$logo_url")" == 404 ]] && pass "null removes the upload" || fail "remove upload"

echo "Genres, tags and the dictionary"
body="$(curl -sS -H "$A" "$base/KasuMeta/Items/$cabin/All")"
[[ "$(json "bool(d['Locales'].get('en', {}).get('Tags', {}).get('Values'))" <<<"$body")" == "True" ]] \
  && pass "TMDB's keywords are stored as English tags" || fail "base tags: $body"
tag="$(json "d['Locales']['en']['Tags']['Values'][0]" <<<"$body")"
[[ "$(json "d['Locales']['es-MX']['Genres']['Source']" <<<"$body")" == "tmdb" ]] && pass "TMDB's Mexican genre names are stored" || fail "es-MX genres: $body"
put_terms() { curl -sS -X PUT -H "$A" -H 'Content-Type: application/json' --data "$1" "$base/KasuMeta/Terms/tag/es"; }
trap 'put_terms "$(python -I -c "import json,sys; print(json.dumps({\"Translations\": {sys.argv[1]: None}}))" "$tag")" >/dev/null; rm -rf "$tmp_dir"' EXIT
body="$(curl -sS -H "$A" "$base/KasuMeta/Terms?kind=tag&locale=es")"
[[ "$(json "any(t['Term'] == '''$tag''' and t['Items'] > 0 for t in d['Terms'])" <<<"$body")" == "True" ]] \
  && pass "the dictionary lists the tags in use" || fail "terms: $body"
put_terms "$(python -I -c "import json,sys; print(json.dumps({'Translations': {sys.argv[1]: 'e2e-traducida'}}))" "$tag")" >/dev/null
body="$(batch "{\"ItemIds\":[\"$cabin\"],\"Locale\":\"es-MX\",\"Fields\":[\"Tags\"],\"Images\":[]}")"
[[ "$(json "d['Items']['$cabin']['Tags']['Source'] == 'dictionary' and 'e2e-traducida' in d['Items']['$cabin']['Tags']['Values']" <<<"$body")" == "True" ]] \
  && pass "a Spanish translation is served to es-MX at once, without a re-fetch" || fail "dictionary: $body"
put_terms "$(python -I -c "import json,sys; print(json.dumps({'Translations': {sys.argv[1]: None}}))" "$tag")" >/dev/null
rm -rf "$tmp_dir"
trap - EXIT

echo "Items by genre or tag"
# by_term KIND TERM LOCALE [EXTRA]: the term is percent-encoded as UTF-8 by Python, as in search.
by_term() {
  local t
  t="$(python -I -c "import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1]))" "$2")"
  curl -sS -H "$U" "$base/KasuMeta/Items/ByTerm?kind=$1&term=$t&locale=$3${4:+&$4}"
}
[[ "$(curl -sS -H "$U" "$base/KasuMeta/Info" | json "'browse' in d['Features']")" == "True" ]] && pass "Info lists the browse feature" || fail "features"
body="$(by_term genre Terror es-MX)"
[[ "$(json "'$cabin' in [r['ItemId'] for r in d['Results']] and '$antitrust' not in [r['ItemId'] for r in d['Results']] and d['Total'] == len(d['Results'])" <<<"$body")" == "True" ]] \
  && pass "the Spanish genre name finds the items shown it (Jellyfin's own is Horror)" || fail "by term: $body"
body="$(by_term genre "ACCION" es_mx)"
[[ "$(json "'$antitrust' in [r['ItemId'] for r in d['Results']] and d['Locale'] == 'es-MX'" <<<"$body")" == "True" ]] \
  && pass "case and accents ignored (ACCION finds Acción)" || fail "by term folded: $body"
[[ "$(by_term genre Terror en-US | json "d['Total']")" == 0 ]] && pass "an English reader isn't shown Terror, so it finds nothing" || fail "by term locale"
body="$(by_term genre Drama es-MX "limit=1")"
[[ "$(json "len(d['Results']) == 1 and d['Total'] >= 2" <<<"$body")" == "True" ]] && pass "paged, with the total" || fail "by term paging: $body"
[[ "$(status -H "$U" "$base/KasuMeta/Items/ByTerm?kind=studio&term=x&locale=es-MX")" == 400 ]] && pass "an unknown kind -> 400" || fail "400 kind"
[[ "$(status "$base/KasuMeta/Items/ByTerm?kind=genre&term=Terror&locale=es-MX")" == 401 ]] && pass "no token -> 401" || fail "401 by term"

echo "Errors"
[[ "$(curl -sS -H "$U" "$base/KasuMeta/Items/$antitrust?locale=pt-BR" | json "d['Name']['ResolvedLocale']")" == "en-US" ]] \
  && pass "no Portuguese: served in the original language (English)" || fail "original-language fallback"
[[ "$(status -H "$U" "$base/KasuMeta/Items/$twin?locale=pt-BR")" == 404 ]] && pass "nothing in any language -> 404" || fail "404"
[[ "$(status -H "$U" "$base/KasuMeta/Items/$antitrust?locale=bad!")" == 400 ]] && pass "bad locale -> 400" || fail "400 locale"
[[ "$(status -X POST -H 'Content-Type: application/json' --data '{"ItemIds":[],"Locale":"es-MX"}' "$base/KasuMeta/Items/Batch")" == 401 ]] \
  && pass "no token -> 401" || fail "401"
many="$(python -I -c "import json,uuid; print(json.dumps([uuid.uuid4().hex for _ in range(201)]))")"
[[ "$(status -X POST -H "$U" -H 'Content-Type: application/json' --data "{\"ItemIds\":$many,\"Locale\":\"es-MX\"}" "$base/KasuMeta/Items/Batch")" == 400 ]] \
  && pass "201 ids -> 400" || fail "400 size"

echo "Visibility"
movies_lib="$(curl -sS -H "$A" "$base/Library/VirtualFolders" | json "[f['ItemId'] for f in d if f['Name']=='Test Videos - Movies'][0]")"
set_folders() {
  local policy
  policy="$(curl -sS -H "$A" "$base/Users/$user_id" | json "json.dumps({**d['Policy'], 'EnableAllFolders': $1, 'EnabledFolders': $2})")"
  curl -sS --fail-with-body -o /dev/null -X POST -H "$A" -H 'Content-Type: application/json' --data "$policy" "$base/Users/$user_id/Policy"
}
trap 'set_folders True "[]"' EXIT
set_folders False "['$movies_lib']"
body="$(batch "{\"ItemIds\":[\"$antitrust\",\"$magia\"],\"Locale\":\"es-MX\"}")"
[[ "$(json "list(d['Items']) == ['$antitrust'] and d['Missing'] == ['$magia']" <<<"$body")" == "True" ]] \
  && pass "a series outside the user's libraries is Missing" || fail "visibility: $body"
[[ "$(status -H "$U" "$base/KasuMeta/Items/$magia?locale=es-MX")" == 404 ]] && pass "and 404 on its own" || fail "hidden 404"
[[ "$(search "magia record" es-MX | json "len(d['Results'])")" == 0 ]] && pass "and not found by search" || fail "search leaks a hidden title"
[[ "$(search "cabana" es-MX | json "d['Results'][0]['ItemId']")" == "$cabin" ]] && pass "while visible titles still are" || fail "search visible"
set_folders True "[]"
trap - EXIT
[[ "$(status -H "$U" "$base/KasuMeta/Items/$magia?locale=es-MX")" == 200 ]] && pass "visible again after restoring access" || fail "restore"

echo "Status"
[[ "$(status -H "$U" "$base/KasuMeta/Status")" == 403 ]] && pass "status -> 403 for a non-admin" || fail "403 status"
body="$(curl -sS -H "$A" "$base/KasuMeta/Status")"
[[ "$(json "d['TmdbConfigured'] and d['SearchIndex'] and d['Items']['Total'] == sum(v for k, v in d['Items'].items() if k != 'Total') and d['Items']['Ok'] > 0" <<<"$body")" == "True" ]] \
  && pass "status: TMDB key set, search index on, item counts add up" || fail "status: $body"
[[ "$(json "d['Providers']['tmdb']['Requests'] > 0" <<<"$body")" == "True" ]] \
  && pass "status counts TMDB requests (the refresh above sent some)" || fail "provider counters: $body"

echo "Providers"
plugin_config="$base/Plugins/703b8e8e-7d16-40bd-8f92-87fad8b741ae/Configuration"
saved_config="$(curl -sS -H "$A" "$plugin_config")"
put_config() { curl -sS --fail-with-body -o /dev/null -X POST -H "$A" -H 'Content-Type: application/json' --data "$1" "$plugin_config"; }
# wait_cabin EXPR: like wait_all, for up to 90 s, since a provider change re-fetches the library.
wait_cabin() {
  local i
  for i in $(seq 1 180); do
    [[ "$(all | json "$1")" == "True" ]] && return 0
    sleep 0.5
  done
  return 1
}
status_now="$(curl -sS -H "$A" "$base/KasuMeta/Status")"
if [[ "$(json "bool(d['TvdbApiKey'])" <<<"$saved_config")" != "True" \
   || "$(json "'tvdb' in str(d['Sources'])" <<<"$status_now")" != "False" ]]; then
  echo "  skip TVDB checks: set a TVDB key and list TVDB for no field to run them"
else
  trap 'put_config "$saved_config"' EXIT
  # Text from TVDB first, then TMDB; posters from TMDB only.
  put_config "$(json "json.dumps({**d, 'ProviderRules': [
      {'Category': c, 'Providers': ['tvdb', 'tmdb', 'anilist']} for c in ['name', 'overview', 'tagline', 'genres', 'tags']
    ] + [{'Category': 'image:Primary', 'Providers': ['tmdb']},
         {'Category': 'image:Logo', 'Providers': ['tvdb', 'tmdb']},
         {'Category': 'image:Backdrop', 'Providers': ['tvdb', 'tmdb']}]})" <<<"$saved_config")"
  wait_cabin "d['Locales']['es-MX'].get('Name', {}).get('Source') == 'tvdb'" \
    && pass "with TVDB first for titles, the library is fetched again and TVDB's title wins" || fail "TVDB never won"
  body="$(all)"
  [[ "$(json "d['Locales']['es-MX']['Name']['Value'] == 'La cabaña en el bosque' and d['Locales']['es-ES']['Name']['Value'] == 'La cabaña en el bosque'" <<<"$body")" == "True" ]] \
    && pass "TVDB's one Spanish title is stored for es-MX and es-ES (found by IMDb id: Cabin has no TVDB id)" || fail "tvdb spanish: $body"
  [[ "$(json "d['Locales']['ja-JP']['Tagline']['Source'] == 'tmdb'" <<<"$body")" == "True" ]] \
    && pass "TMDB fills what TVDB lacks (the Japanese tagline)" || fail "tmdb fill: $body"
  [[ "$(json "all(i['Source'] == 'tmdb' for l in d['Locales'].values() for t, i in l.get('Images', {}).items() if t == 'Primary')" <<<"$body")" == "True" ]] \
    && pass "posters stay TMDB's: that category lists TMDB only" || fail "poster sources: $body"
  body="$(curl -sS -H "$A" "$base/KasuMeta/Status")"
  [[ "$(json "d['MetadataProviders'] == ['tvdb', 'tmdb'] and d['Sources']['image:Primary'] == ['tmdb'] and d['Providers']['tvdb']['Requests'] > 0" <<<"$body")" == "True" ]] \
    && pass "status lists the sources per field and counts TVDB's requests" || fail "status: $body"
  put_config "$saved_config"
  trap - EXIT
  wait_cabin "d['Locales']['es-MX'].get('Name', {}).get('Source') == 'tmdb' and 'tvdb' not in str(d)" \
    && pass "back on TMDB only, TMDB's titles return and TVDB's are gone" || fail "restore providers"
fi

echo "Scheduled tasks"
tasks="$(curl -sS -H "$A" "$base/ScheduledTasks")"
[[ "$(json "sorted(t['Key'] for t in d if t['Category']=='KasuMeta')" <<<"$tasks")" == "['KasuMetaBackfill', 'KasuMetaRefreshStale']" ]] \
  && pass "both KasuMeta tasks registered" || fail "tasks"

echo "All end-to-end checks passed."
