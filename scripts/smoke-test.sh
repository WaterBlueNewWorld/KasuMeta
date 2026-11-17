#!/usr/bin/env bash
# Checks a running dev server after dev-server-setup.sh:
#   - the plugin loaded and its database is ready, with FTS5;
#   - /KasuMeta/Info answers 401 without a token and 200 for a signed-in non-admin.
# Exits non-zero on the first failure.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
while IFS='=' read -r key value; do
  [[ -z "$key" || "$key" == \#* ]] && continue
  export "$key=${value%$'\r'}"
done <"$repo_root/.env"

base="${JELLYFIN_URL%/}"
client='MediaBrowser Client="KasuMeta smoke test", Device="smoke-test.sh", DeviceId="kasumeta-smoke", Version="0.0.0"'
container="${KASUMETA_CONTAINER:-kasumeta-jellyfin}"

pass() { printf '  ok   %s\n' "$*"; }
fail() { printf '  FAIL %s\n' "$*" >&2; exit 1; }
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
login() {
  local body
  body="$(curl -sS --fail-with-body -X POST -H "Authorization: $client" -H 'Content-Type: application/json' \
    --data "{\"Username\":\"$1\",\"Pw\":\"$2\"}" "$base/Users/AuthenticateByName")"
  sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p' <<<"$body"
}

echo "Server $(curl -s "$base/System/Info/Public" | sed -n 's/.*"Version":"\([^"]*\)".*/\1/p') at $base"

logs="$(docker logs "$container" 2>&1)"
if grep -q 'Loaded plugin: KasuMeta' <<<"$logs"; then pass "plugin loaded"; else fail "plugin not loaded (docker logs $container)"; fi
if grep -q 'KasuMeta database ready .*FTS5 available: True' <<<"$logs"; then
  pass "database ready, FTS5 available"
else
  fail "database not ready, or FTS5 missing"
fi
if grep -Eq '\[(ERR|FTL)\].*KasuMeta' <<<"$logs"; then fail "KasuMeta errors in the log"; else pass "no KasuMeta errors in the log"; fi

admin_token="$(login "$JELLYFIN_ADMIN_USER" "$JELLYFIN_ADMIN_PASSWORD")"
user_token="$(login "$JELLYFIN_TEST_USER" "$JELLYFIN_TEST_PASSWORD")"
if [[ -n "$admin_token" && -n "$user_token" ]]; then pass "admin and test user signed in"; else fail "sign-in failed"; fi

code="$(status "$base/KasuMeta/Info")"
if [[ "$code" == 401 ]]; then pass "GET /KasuMeta/Info without a token -> 401"; else fail "without a token got $code"; fi

code="$(status -H "Authorization: $client, Token=\"$user_token\"" "$base/KasuMeta/Info")"
if [[ "$code" == 200 ]]; then pass "GET /KasuMeta/Info as a non-admin -> 200"; else fail "as a non-admin got $code"; fi
info="$(curl -sS -H "Authorization: $client, Token=\"$user_token\"" "$base/KasuMeta/Info")"
echo "       $info"
# Jellyfin serializes in PascalCase, like the rest of its API (see api/openapi.yaml).
if grep -q '"ApiVersion":1' <<<"$info"; then pass "ApiVersion 1 (PascalCase, as Jellyfin serializes)"; else fail "unexpected body"; fi
if grep -qi 'key' <<<"$info"; then fail "payload mentions a key"; else pass "no API keys in the payload"; fi

admin_auth="Authorization: $client, Token=\"$admin_token\""
plugins="$(curl -sS -H "$admin_auth" "$base/Plugins")"
if grep -Eq '"Name":"KasuMeta"[^}]*"Status":"Active"|"Status":"Active"[^}]*"Name":"KasuMeta"' <<<"$plugins"; then
  pass "Dashboard lists KasuMeta as Active"
else
  fail "KasuMeta not Active in /Plugins"
fi
page="$(curl -sS -H "$admin_auth" "$base/web/ConfigurationPage?name=KasuMeta")"
if grep -q 'KasuMetaConfigPage' <<<"$page"; then pass "configuration page served"; else fail "configuration page missing"; fi

# MSYS_NO_PATHCONV only here: Git Bash would rewrite /config into a Windows path, but set
# globally it also stops /dev/null from being translated for curl.
if MSYS_NO_PATHCONV=1 docker exec "$container" test -f /config/data/kasumeta/kasumeta.db; then
  pass "database file at /config/data/kasumeta/kasumeta.db"
else
  fail "no database file"
fi

echo "All checks passed."
