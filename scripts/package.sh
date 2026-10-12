#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration=Release

if [[ "${1:-}" == "Debug" || "${1:-}" == "Release" ]]; then
  configuration="$1"
  shift
fi

dotnet build "$repo_root/Blasto.GameMod/Blasto.GameMod.csproj" \
  --configuration "$configuration" \
  --nologo \
  "$@"

build_output="$repo_root/Blasto.GameMod/bin/$configuration/net10.0"
destination="$repo_root/dist/Blasto"

for artifact in Blasto.dll Blasto.deps.json mod.toml; do
  if [[ ! -f "$build_output/$artifact" ]]; then
    printf 'Blasto package is missing required build output: %s\n' "$build_output/$artifact" >&2
    exit 1
  fi
done

mkdir -p "$repo_root/dist"
staging="$(mktemp -d "$repo_root/dist/.Blasto-stage.XXXXXX")"
trap 'rm -rf "$staging"' EXIT

cp "$build_output/Blasto.dll" "$build_output/Blasto.deps.json" "$build_output/mod.toml" "$staging/"

# This prototype has no privately shipped runtime dependencies. KSA and the StarMap
# loader provide their assemblies; the allow-list above keeps them out of the payload.
if find "$staging" -type f \( -name 'KSA.dll' -o -name 'Brutal.*.dll' -o -name 'Planet.*.dll' \
    -o -name 'StarMap.API.dll' -o -name '0Harmony.dll' \) -print -quit | grep -q .; then
  printf 'Blasto package unexpectedly contains game or loader assemblies.\n' >&2
  exit 1
fi

rm -rf "$destination"
mv "$staging" "$destination"
trap - EXIT

printf 'Packaged Blasto to %s\n' "$destination"
