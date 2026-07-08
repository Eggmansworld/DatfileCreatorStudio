#!/bin/sh
# Produces a self-contained, single-file Linux x64 build of Datfile Creator
# Studio in dist/linux-x64. The target machine needs no .NET runtime installed.
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUNTIME="${1:-linux-x64}"
CONFIGURATION="${2:-Release}"

OUTPUT="$SCRIPT_DIR/dist/$RUNTIME"
rm -rf "$OUTPUT"

dotnet publish "$SCRIPT_DIR/src/DatfileCreatorStudio/DatfileCreatorStudio.csproj" \
    -c "$CONFIGURATION" -r "$RUNTIME" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -p:DebugSymbols=false \
    -o "$OUTPUT"

echo ""
echo "Datfile Creator Studio published to $OUTPUT"
