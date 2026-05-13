#!/usr/bin/env bash
set -euo pipefail

# dist.all.sh — Build native macOS bridge and pack LeXtudio.UI.Text.Core nupkg.
# Must run on macOS (AppKit is required to build libUnoEditMacInput.dylib).
# Usage: ./dist.all.sh [Configuration] [PackageVersion]
# Example: ./dist.all.sh Release 0.2.1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

UNAME_STR="$(uname -s 2>/dev/null || echo unknown)"
if [[ "$UNAME_STR" != Darwin* ]]; then
  echo "Error: dist.all.sh must run on macOS (AppKit required for libUnoEditMacInput.dylib)." >&2
  exit 1
fi

CONFIG=${1:-Release}
PKGVER=${2:-0.2.1}
PROJECT="src/LeXtudio.UI.Text.Core/LeXtudio.UI.Text.Core.csproj"
OUT_DIR="$SCRIPT_DIR/dist"

echo "dist.all.sh: Configuration=$CONFIG PackageVersion=$PKGVER"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "Restoring packages..."
dotnet restore "$PROJECT"

echo "Building Uno desktop target and native macOS bridge..."
dotnet build "$PROJECT" -c "$CONFIG" /t:Build,BuildTextCoreMacInputBridge

echo "Packing nupkg to $OUT_DIR (unsigned)..."
dotnet pack "$PROJECT" -c "$CONFIG" -o "$OUT_DIR" --no-build /p:PackageVersion="$PKGVER"

echo "Pack output:"
ls -la "$OUT_DIR" || true

echo "Verifying runtimes entries in package:"
for f in "$OUT_DIR"/*.nupkg; do
  echo "--- $f ---"
  unzip -l "$f" | grep -E 'runtimes/.+libUnoEditMacInput.dylib' || echo "(no runtimes entry found)"
done

echo "Done. Unsigned packages are in $OUT_DIR"
