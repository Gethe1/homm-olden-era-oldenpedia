#!/usr/bin/env bash
#
# Build OldenPedia and install the DLL into the game's BepInEx/plugins folder.
#
# Usage:
#   ./build.sh [path-to-game-dir]
#
# The game dir is resolved in this order:
#   1. the first argument, if given
#   2. $OLDENPEDIA_GAMEDIR, if set
#   3. the common native-Steam default below
#
# Other Steam layouts (override via arg or env var):
#   Flatpak Steam:  ~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Heroes of Might and Magic Olden Era
#   Steam Deck SD:  /run/media/deck/<card>/steamapps/common/Heroes of Might and Magic Olden Era
#
set -euo pipefail

DEFAULT="$HOME/.steam/steam/steamapps/common/Heroes of Might and Magic Olden Era"
GAMEDIR="${1:-${OLDENPEDIA_GAMEDIR:-$DEFAULT}}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: 'dotnet' not found. Install the .NET SDK 6+ first." >&2
  exit 1
fi

if [[ ! -d "$GAMEDIR/BepInEx/interop" ]]; then
  echo "ERROR: '$GAMEDIR/BepInEx/interop' not found." >&2
  echo "Install the BepInEx 6 IL2CPP pack, set the WINEDLLOVERRIDES launch" >&2
  echo "option, and launch the game once via Proton so the interop is built." >&2
  exit 1
fi

echo ">> Building against: $GAMEDIR"
dotnet build -c Release -p:GameDir="$GAMEDIR"

PLUGINS="$GAMEDIR/BepInEx/plugins"
mkdir -p "$PLUGINS"
cp -v bin/Release/net6.0/OldenPedia.dll "$PLUGINS/"
echo ">> Installed OldenPedia.dll -> $PLUGINS"
echo ">> Launch the game; press '.' for the window."
echo ">> See README.md's 'Dev probes' table for the current probe keys (they've"
echo ">> changed over time — don't hardcode a specific list here again)."
