#!/usr/bin/env bash
# Launch a plain play session with the mod and no test harness, for building
# craft by hand and saving them for later inspection.
#
#   test/integration/play.sh              # on your desktop (:0)
#   test/integration/play.sh --no-mod     # stock B9, to compare against
#   DS_DISPLAY=:1 test/integration/play.sh
#
# Audio is turned back ON here, unlike every automated launch, which mutes it.
# Debug logging is left on so that whatever you do is recorded in KSP.log - copy
# that file somewhere before the next automated run, which truncates it.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ksp="$root/testenv/KSP"
display="${DS_DISPLAY:-:0}"
plugins="$ksp/GameData/DimensionSync/Plugins"

# --no-mod runs stock B9 with this mod out of the way, for telling "B9 does this
# too" apart from "we did this". The DLL is moved aside rather than deleted, and
# put back by the next run without the flag.
withmod=1
[[ "${1:-}" == "--no-mod" ]] && withmod=0

if (( withmod )); then
    echo "==> building"
    dotnet build "$root/src/DimensionSync.csproj" -v quiet --nologo

    echo "==> deploying"
    mkdir -p "$plugins"
    [[ -f "$plugins/DimensionSync.dll.off" ]] && rm -f "$plugins/DimensionSync.dll.off"
    cp "$root/GameData/DimensionSync/Plugins/DimensionSync.dll" "$plugins/"
    sed -i 's/^\(\s*\)debug = false/\1debug = true/' "$ksp/GameData/DimensionSync/DimensionSync.cfg"
else
    echo "==> running WITHOUT DimensionSync (stock B9 only)"
    [[ -f "$plugins/DimensionSync.dll" ]] && mv "$plugins/DimensionSync.dll" "$plugins/DimensionSync.dll.off"
fi

# The automated scripts mute the game so a background run stays quiet. A session
# somebody is actually sitting at wants sound, so put it back.
sed -i -E 's/^(MASTER_VOLUME|SHIP_VOLUME|AMBIENCE_VOLUME|MUSIC_VOLUME|UI_VOLUME|VOICE_VOLUME) = 0$/\1 = 0.5/' \
    "$ksp/settings.cfg"

echo "==> launching on $display"
cd "$ksp"
exec env DISPLAY="$display" ./KSP.x86_64
