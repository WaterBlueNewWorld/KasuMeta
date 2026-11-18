#!/usr/bin/env bash
# One-time AniList setup for the dev server, after dev-server-setup.sh and the library scan.
#
#   bash scripts/dev-anilist-setup.sh
#
# The dev server has no AniList metadata plugin, so its anime series carry no AniList ids. This
# sets them by hand, as the dashboard's "Edit metadata > External IDs" would. Each change fires
# ItemUpdated, so KasuMeta fetches the series from AniList within seconds. ja-Latn is left out of
# the enabled locales on purpose: romaji is stored for search anyway, and that is what the e2e
# script checks. Safe to re-run: each step checks first.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
while IFS='=' read -r key value; do
  [[ -z "$key" || "$key" == \#* ]] && continue
  export "$key=${value%$'\r'}"
done <"$repo_root/.env"

base="${JELLYFIN_URL%/}"
client='MediaBrowser Client="KasuMeta setup", Device="dev-anilist-setup.sh", DeviceId="kasumeta-anilist-setup", Version="0.0.0"'
json() { python -I -c "import sys,json; sys.stdout.reconfigure(encoding='utf-8'); d=json.load(sys.stdin); print($1)"; }
log() { printf '[anilist] %s\n' "$*"; }

login="$(curl -sS --fail-with-body -X POST -H "Authorization: $client" -H 'Content-Type: application/json' \
  --data "{\"Username\":\"$JELLYFIN_ADMIN_USER\",\"Pw\":\"$JELLYFIN_ADMIN_PASSWORD\"}" "$base/Users/AuthenticateByName")"
A="Authorization: $client, Token=\"$(json "d['AccessToken']" <<<"$login")\""
admin_id="$(json "d['User']['Id']" <<<"$login")"

# set_anilist SERIES_NAME ANILIST_ID (ids looked up on anilist.co for the test media)
set_anilist() {
  local items id dto
  items="$(curl -sS --fail-with-body -H "$A" "$base/Items?Recursive=true&IncludeItemTypes=Series")"
  id="$(json "([i['Id'] for i in d['Items'] if i['Name']=='$1'] or [''])[0]" <<<"$items")"
  [[ -n "$id" ]] || { echo "Series '$1' not found. Has the library scan finished?" >&2; exit 1; }
  dto="$(curl -sS --fail-with-body -H "$A" "$base/Users/$admin_id/Items/$id")"
  if [[ "$(json "d['ProviderIds'].get('AniList')" <<<"$dto")" == "$2" ]]; then
    log "$1 already has AniList id $2."
    return
  fi

  # The whole item goes back, as the dashboard's editor sends it; only ProviderIds changes.
  dto="$(json "json.dumps({**d, 'ProviderIds': {**d['ProviderIds'], 'AniList': '$2'}})" <<<"$dto")"
  curl -sS --fail-with-body -o /dev/null -X POST -H "$A" -H 'Content-Type: application/json' --data "$dto" "$base/Items/$id"
  log "$1: AniList id $2 set."
}
set_anilist 'Magia Record: Puella Magi Madoka Magica Side Story' 104051
set_anilist 'Revue Starlight' 98658
log "Done. KasuMeta fetches both series from AniList within a few seconds."
