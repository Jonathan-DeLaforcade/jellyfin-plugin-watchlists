#!/usr/bin/env bash
# Build sans accès NuGet : compile contre les stubs de signatures (build/stubs), lance les tests,
# puis prépare dist/Watchlists_<version>/ prêt à copier dans le dossier plugins de Jellyfin.
set -euo pipefail
cd "$(dirname "$0")"
dotnet build build/stubs/MediaBrowser.Controller -c Release -o build/stubs/out
dotnet build src/Jellyfin.Plugin.Watchlists -c Release -p:UseStubs=true
dotnet run --project tests/Watchlists.ApiTests -c Release -p:UseStubs=true
if command -v node >/dev/null && [ -d tests/ui/node_modules ]; then (cd tests/ui && node ui-test.mjs); fi
VERSION=$(grep -oP '(?<="version": ")[^"]+' meta.json)
OUT=dist/Watchlists_$VERSION
rm -rf "$OUT" && mkdir -p "$OUT"
cp src/Jellyfin.Plugin.Watchlists/bin/Release/net10.0/Jellyfin.Plugin.Watchlists.dll meta.json "$OUT/"
echo "OK -> $OUT"
