#!/usr/bin/env bash
set -euo pipefail

VERSION="1.0.0"
DLL="bin/Release/netstandard2.1/MiniEepo.dll"
OUT="MiniEepo-${VERSION}.zip"
R2_PROFILE="${R2_PROFILE:-10-30-update}"
R2_PLUGINS="$HOME/.config/r2modmanPlus-local/REPO/profiles/$R2_PROFILE/BepInEx/plugins/MiniEepo"

# Resolve game directory for game DLLs (Assembly-CSharp, UnityEngine).
# BepInEx and ScalerCore are handled via NuGet/libs, so GameDir is only
# needed for the REPO_Data/Managed assemblies.
if [ -z "${GAME_DIR:-}" ]; then
    for candidate in \
        "$HOME/.steam/steam/steamapps/common/REPO" \
        "$HOME/.local/share/Steam/steamapps/common/REPO"
    do
        if [ -d "$candidate" ]; then
            GAME_DIR="$candidate"
            break
        fi
    done
fi

if [ -z "${GAME_DIR:-}" ]; then
    echo "ERROR: Could not find R.E.P.O. install. Set GAME_DIR manually:"
    echo "  GAME_DIR=\"/path/to/REPO\" ./package.sh"
    exit 1
fi

echo "Using game dir: $GAME_DIR"

# Build
dotnet build MiniEepo.csproj --configuration Release /p:GameDir="$GAME_DIR"

# Deploy to r2modman profile for local testing
mkdir -p "$R2_PLUGINS"
cp "$DLL" "$R2_PLUGINS/"
echo "Deployed to r2modman profile: $R2_PROFILE"

# Verify icon exists before packaging
if [ ! -f "icon.png" ]; then
    echo "WARNING: icon.png not found — skipping Thunderstore zip."
    exit 0
fi

# Package
rm -f "$OUT"
zip -j "$OUT" manifest.json icon.png README.md "$DLL"
echo "Packaged: $OUT"
