#!/bin/bash
set -e
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
SRC="/mnt/c/dev/ValheimEnvMod/Pilgrim/bin/Release/netstandard2.1/Pilgrim.dll"
DST="/mnt/c/Users/curti/AppData/Roaming/r2modmanPlus-local/Valheim/profiles/onepoint0/BepInEx/plugins/Unknown-Pilgrim/Pilgrim.dll"

echo "Building..."
cd /mnt/c/dev/ValheimEnvMod/Pilgrim
"$DOTNET" build -c Release -v quiet

echo "Copying..."
mkdir -p "$(dirname "$DST")"
cp "$SRC" "$DST"

echo "Done. Relaunch Valheim to load the new DLL."
