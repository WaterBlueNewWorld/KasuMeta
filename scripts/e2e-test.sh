#!/usr/bin/env bash
# End-to-end checks of the read path against the dev server, after dev-server-setup.sh and with
# a TMDB key set on the plugin page. Uses the test media's real titles, so it depends on what TMDB
# returns for them; if TMDB edits a translation, update the expected strings below.
#
#   bash scripts/e2e-test.sh
#
# Temporarily restricts the test user to the Movies library (visibility check) and restores it.
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

echo "Errors"
[[ "$(status -H "$U" "$base/KasuMeta/Items/$antitrust?locale=pt-BR")" == 404 ]] && pass "no translation -> 404" || fail "404"
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
set_folders True "[]"
trap - EXIT
[[ "$(status -H "$U" "$base/KasuMeta/Items/$magia?locale=es-MX")" == 200 ]] && pass "visible again after restoring access" || fail "restore"

echo "Scheduled tasks"
tasks="$(curl -sS -H "$A" "$base/ScheduledTasks")"
[[ "$(json "sorted(t['Key'] for t in d if t['Category']=='KasuMeta')" <<<"$tasks")" == "['KasuMetaBackfill', 'KasuMetaRefreshStale']" ]] \
  && pass "both KasuMeta tasks registered" || fail "tasks"

echo "All end-to-end checks passed."
