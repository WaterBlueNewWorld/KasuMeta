#!/usr/bin/env bash
# Checks that every place naming the plugin version agrees:
#   Directory.Build.props <Version>           1.0.0.0    (the assembly version)
#   build.yaml version                        "1.0.0.0"  (the catalog and the zip name)
#   docker/dev/compose.yaml plugin folder     KasuMeta_1.0.0.0
#   README.md install path                    KasuMeta_1.0.0.0
# and, when a tag is given (the release workflow passes it), that the tag is v1.0.0.
#
#   bash scripts/check-version.sh [v1.0.0]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# four_part 0.1 → 0.1.0.0, 0.1.0 → 0.1.0.0, 0.1.0.0 stays.
four_part() {
  local IFS=. parts
  read -ra parts <<<"$1"
  while ((${#parts[@]} < 4)); do parts+=(0); done
  printf '%s.%s.%s.%s' "${parts[@]:0:4}"
}

props="$(sed -n 's:.*<Version>\([0-9.]*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
manifest="$(sed -n 's/^version: *"\{0,1\}\([0-9.]*\)"\{0,1\} *$/\1/p' build.yaml | head -1)"
compose="$(sed -n 's:.*dest=/config/plugins/KasuMeta_\([0-9.]*\).*:\1:p' docker/dev/compose.yaml | head -1)"
readme="$(sed -n 's:.*plugins/KasuMeta_\([0-9.]*\)/.*:\1:p' README.md | head -1)"

[[ -n "$props" ]] || { echo "No <Version> in Directory.Build.props" >&2; exit 1; }
expected="$(four_part "$props")"
status=0
check() { # NAME VALUE
  if [[ "$2" == "$expected" ]]; then
    printf '  ok   %-40s %s\n' "$1" "$2"
  else
    printf '  FAIL %-40s %s (expected %s)\n' "$1" "${2:-missing}" "$expected" >&2
    status=1
  fi
}

echo "Plugin version $expected (Directory.Build.props: $props)"
check "build.yaml version" "$manifest"
check "docker/dev/compose.yaml plugin folder" "$compose"
check "README.md install path" "$readme"
if [[ $# -gt 0 ]]; then
  tag="$1"
  if [[ "$tag" != v* ]]; then
    printf '  FAIL %-40s %s (tags look like v%s)\n' "release tag" "$tag" "$props" >&2
    status=1
  else
    check "release tag $tag" "$(four_part "${tag#v}")"
  fi
fi

exit "$status"
