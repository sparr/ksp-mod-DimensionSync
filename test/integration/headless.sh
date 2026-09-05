#!/usr/bin/env bash
# Run KSP headless on Xvfb with everything an automated run should have, and
# nothing it should not.
#
#   test/integration/headless.sh -dstest -dstest-only probe_ -dstest-out /tmp/r.json
#
# Exists because the same three lines kept getting typed by hand and the audio
# one kept getting left out: KSP plays through FMOD straight to the system mixer,
# so a run on a hidden display is still audible. A play session turns the volumes
# back up, so the next hand-rolled run is loud unless it mutes them again.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ksp="$root/testenv/KSP"

sed -i -E 's/^(MASTER_VOLUME|SHIP_VOLUME|AMBIENCE_VOLUME|MUSIC_VOLUME|UI_VOLUME|VOICE_VOLUME) = .*/\1 = 0/' \
    "$ksp/settings.cfg"

# Build and deploy here rather than trusting the caller to have done it. A run
# against a stale assembly is worse than no run: it produces numbers that look
# like measurements of the change being tested and are measurements of the one
# before it. Failing builds went unnoticed for an hour that way, because the
# interesting line is above the summary and the summary is what gets read.
echo "==> building"
dotnet build "$root/src/DimensionSync.csproj" -v quiet --nologo
dotnet build "$root/test/DimensionSync.GameTests/DimensionSync.GameTests.csproj" -v quiet --nologo
cp "$root/GameData/DimensionSync/Plugins/DimensionSync.dll" "$ksp/GameData/DimensionSync/Plugins/"
sed -i 's/^\(\s*\)debug = false/\1debug = true/' "$ksp/GameData/DimensionSync/DimensionSync.cfg"

# Refuse to run against an assembly older than the source that should have built
# it. set -e already stops on a failed build; this catches the subtler case where
# the build succeeded but wrote somewhere else.
for pair in "$root/src:$ksp/GameData/DimensionSync/Plugins/DimensionSync.dll" \
            "$root/test/DimensionSync.GameTests:$ksp/GameData/DimensionSyncGameTests/DimensionSync.GameTests.dll"; do
    src="${pair%%:*}"; dll="${pair##*:}"
    if [[ -f "$dll" ]] && [[ -n "$(find "$src" -name '*.cs' -newer "$dll" -print -quit)" ]]; then
        echo "deployed $(basename "$dll") is older than its sources - build did not reach it" >&2
        exit 1
    fi
done

cd "$ksp"
# Keep the previous log. Every automated run truncates it, and the one being
# thrown away is regularly the one somebody wanted.
[[ -f KSP.log ]] && mv -f KSP.log KSP.previous.log
: > KSP.log
LIBGL_ALWAYS_SOFTWARE=1 timeout "${DS_TIMEOUT:-420}" \
    xvfb-run -n 99 -s "-screen 0 1024x768x24" \
    ./KSP.x86_64 -force-glcore "$@" > run.out 2>&1 || true
