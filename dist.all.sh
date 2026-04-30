#!/usr/bin/env bash
set -euo pipefail

# dist.all.sh - Build native macOS bridge and pack unsigned nupkgs (macOS)
# Usage: dist.all.sh [Configuration] [PackageVersion]
# Example: ./dist.all.sh Release 0.2.1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

CONFIG=${1:-Release}
PKGVER=${2:-0.2.1}
PROJECT="src/LeXtudio.UI.Text.Core.csproj"
OUT_DIR="$SCRIPT_DIR/dist"

echo "dist.all.sh: Configuration=$CONFIG PackageVersion=$PKGVER"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "Building project and native macOS bridge..."
dotnet build "$PROJECT" -c "$CONFIG" -f net9.0-desktop /t:Build,BuildTextCoreMacInputBridge

echo "Packing nupkg(s) to $OUT_DIR (unsigned)..."
dotnet pack "$PROJECT" -c "$CONFIG" -o "$OUT_DIR" --no-build /p:PackageVersion="$PKGVER"

echo "Pack output:" 
ls -la "$OUT_DIR"

echo "Verifying package contents (runtimes entries):"
for f in "$OUT_DIR"/*.nupkg; do
  echo "--- $f ---"
  unzip -l "$f" | grep -E 'runtimes/.+libUnoEditMacInput.dylib' || true
done

echo "Done. Unsigned packages are in $OUT_DIR"
