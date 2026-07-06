#!/bin/bash
# Run from main after merging dev. Bumps version, builds, and uploads to Thunderstore.
# Usage: bash publish.sh 0.2.0 YOUR_THUNDERSTORE_TOKEN
set -e

VERSION=$1
TOKEN=$2

if [ -z "$VERSION" ] || [ -z "$TOKEN" ]; then
    echo "Usage: bash publish.sh <version> <thunderstore_token>"
    exit 1
fi

DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
TCLI="/mnt/c/Users/curti/.dotnet/tools/tcli.exe"

# Bump version
sed -i "s/versionNumber = \".*\"/versionNumber = \"$VERSION\"/" thunderstore.toml
sed -i "s/BepInPlugin(\"com.ctogle.pilgrim\", \"Pilgrim\", \".*\")/BepInPlugin(\"com.ctogle.pilgrim\", \"Pilgrim\", \"$VERSION\")/" Pilgrim/Plugin.cs

echo "Building $VERSION..."
"$DOTNET" build Pilgrim/Pilgrim.csproj -c Release -v quiet

echo "Publishing to Thunderstore..."
"$TCLI" publish --token "$TOKEN"

echo "Done. Tag and push when ready: git tag v$VERSION && git push origin main --tags"
