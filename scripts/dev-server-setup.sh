#!/usr/bin/env bash
# First-run setup for KasuMeta's Jellyfin 12.1 dev server (docker/dev/compose.yaml).
# Modeled on the client's scripts/dev-server-setup.sh, with its own credentials and port.
#
# Drives /Startup so nobody clicks through the wizard, then makes sure the two test libraries
# exist (over the client's test-media) and starts a scan. Safe to re-run: each step checks first.
#
# Reads .env at the repo root, creating it with random passwords on the first run (git-ignored).
# Needs: bash, curl, sed (Git Bash on Windows works).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_file="$repo_root/.env"

if [[ ! -f "$env_file" ]]; then
  rand() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 24; }
  cat >"$env_file" <<ENV
# KasuMeta dev server (docker/dev/compose.yaml). Local only; never commit.
JELLYFIN_URL=http://localhost:8097
JELLYFIN_ADMIN_USER=kasumeta-admin
JELLYFIN_ADMIN_PASSWORD=$(rand)
JELLYFIN_TEST_USER=kasumeta-user
JELLYFIN_TEST_PASSWORD=$(rand)
ENV
  echo "[setup] Created $env_file with random passwords."
fi

while IFS='=' read -r key value; do
  [[ -z "$key" || "$key" == \#* ]] && continue
  export "$key=${value%$'\r'}"
done <"$env_file"
: "${JELLYFIN_URL:?}" "${JELLYFIN_ADMIN_USER:?}" "${JELLYFIN_ADMIN_PASSWORD:?}" "${JELLYFIN_TEST_USER:?}" "${JELLYFIN_TEST_PASSWORD:?}"

base="${JELLYFIN_URL%/}"
client_auth='MediaBrowser Client="KasuMeta dev setup", Device="dev-server-setup.sh", DeviceId="kasumeta-dev-setup", Version="0.0.0"'
auth_header="$client_auth"
log() { printf '[setup] %s\n' "$*"; }
json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }
url_encode() {
  local s="$1" out="" c i
  for ((i = 0; i < ${#s}; i++)); do
    c="${s:i:1}"
    case "$c" in [a-zA-Z0-9.~_-]) out+="$c" ;; *) out+="$(printf '%%%02X' "'$c")" ;; esac
  done
  printf '%s' "$out"
}
api() {
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS --fail-with-body -X "$method" -H "Authorization: $auth_header")
  [[ -n "$body" ]] && args+=(-H 'Content-Type: application/json' --data "$body")
  curl "${args[@]}" "$base$path"
}

log "Waiting for $base ..."
info=""
for _ in $(seq 1 60); do
  info="$(curl -s --max-time 5 "$base/System/Info/Public" || true)"
  [[ "$info" == \{* ]] && break
  sleep 2
done
[[ "$info" == \{* ]] || { echo "Server at $base did not come up. Is kasumeta-jellyfin running?" >&2; exit 1; }
log "Server is up: $(sed -n 's/.*"Version":"\([^"]*\)".*/Jellyfin \1/p' <<<"$info")"

if grep -q '"StartupWizardCompleted":false' <<<"$info"; then
  log "Running the setup wizard ..."
  api POST /Startup/Configuration \
    '{"ServerName":"KasuMeta Dev","UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
  api GET /Startup/User >/dev/null # the server creates the first user lazily
  api POST /Startup/User \
    "{\"Name\":\"$(json_escape "$JELLYFIN_ADMIN_USER")\",\"Password\":\"$(json_escape "$JELLYFIN_ADMIN_PASSWORD")\"}" >/dev/null
  api POST /Startup/RemoteAccess '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null
  api POST /Startup/Complete >/dev/null
  log "Wizard complete; admin user is '$JELLYFIN_ADMIN_USER'."
else
  log "Setup wizard already completed; skipping."
fi

login="$(api POST /Users/AuthenticateByName \
  "{\"Username\":\"$(json_escape "$JELLYFIN_ADMIN_USER")\",\"Pw\":\"$(json_escape "$JELLYFIN_ADMIN_PASSWORD")\"}")" || {
  echo "Admin login failed. Do .env and the server agree? (Reset: docker compose -f docker/dev/compose.yaml down -v)" >&2
  exit 1
}
token="$(sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p' <<<"$login")"
[[ -n "$token" ]] || { echo "No access token in login response." >&2; exit 1; }
auth_header="$client_auth, Token=\"$token\""
log "Logged in as '$JELLYFIN_ADMIN_USER'."

# Typed libraries, not one mixed library: a mixed one resolves every video as a Movie.
folders="$(api GET /Library/VirtualFolders)"
ensure_library() {
  local name="$1" type="$2" path="$3"
  if grep -q "\"Name\":\"$name\"" <<<"$folders"; then
    log "Library '$name' already exists; skipping."
  else
    log "Creating $type library '$name' at $path ..."
    api POST "/Library/VirtualFolders?name=$(url_encode "$name")&collectionType=$type&paths=$(url_encode "$path")&refreshLibrary=false" \
      '{"LibraryOptions":{}}' >/dev/null
  fi
}
ensure_library "Test Videos - Movies" movies /media/movies
ensure_library "Test Videos - Shows" tvshows /media/shows

users="$(api GET /Users)"
if grep -q "\"Name\":\"$(json_escape "$JELLYFIN_TEST_USER")\"" <<<"$users"; then
  log "Test user '$JELLYFIN_TEST_USER' already exists; skipping."
else
  log "Creating non-admin test user '$JELLYFIN_TEST_USER' ..."
  api POST /Users/New \
    "{\"Name\":\"$(json_escape "$JELLYFIN_TEST_USER")\",\"Password\":\"$(json_escape "$JELLYFIN_TEST_PASSWORD")\"}" >/dev/null
fi

log "Starting a library scan (runs in the background) ..."
api POST /Library/Refresh >/dev/null
log "Done. Server: $base  (credentials in .env)"
