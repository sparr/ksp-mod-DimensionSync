#!/usr/bin/env bash
# Load one of the player's saved craft and write down how its wings and control
# surfaces are actually oriented.
#
# The scenarios build every fixture programmatically, with orientations set
# explicitly, so they produce identical geometry in either editor and can never
# reproduce something that comes from how KSP orients a part when a PERSON places
# it. This reads a craft somebody actually built instead.
#
#   ./test/integration/inspect_craft.sh "My Test Plane"
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ksp="$root/testenv/KSP"
craft="${1:-}"

# --edit makes the same change twice: once on the craft as loaded, and once after
# leaving the editor and coming back. That difference is the whole point - the same
# craft and the same edit, where only one of the two misbehaves.
#
# A third argument is passed through as -dstest-inspect-set, so the change made
# can be the one worth asking about:
#
#   ./test/integration/inspect_craft.sh "My Craft" --edit "ctrl:sharedBaseLength=2"
edit=()
[[ "${2:-}" == "--edit" ]] && edit+=(-dstest-inspect-edit)
[[ -n "${3:-}" ]] && edit+=(-dstest-inspect-set "$3")

if [[ -z "$craft" ]]; then
    echo "usage: $0 <craft name>" >&2
    echo "available craft:" >&2
    find "$ksp/saves" -name '*.craft' -printf '  %f\n' 2>/dev/null | sed 's/\.craft$//' >&2
    exit 2
fi

echo "==> building"
dotnet build "$root/src/DimensionSync.csproj" -v quiet --nologo
dotnet build "$root/test/DimensionSync.GameTests/DimensionSync.GameTests.csproj" -v quiet --nologo

echo "==> deploying"
cp "$root/GameData/DimensionSync/Plugins/DimensionSync.dll" "$ksp/GameData/DimensionSync/Plugins/"

# Debug on: the point of this is to see every decision the mod makes about the
# craft, not just its final state.
sed -i 's/^\(\s*\)debug = false/\1debug = true/' "$ksp/GameData/DimensionSync/DimensionSync.cfg"

# Muted, like every other automated launch.
sed -i -E 's/^(MASTER_VOLUME|SHIP_VOLUME|AMBIENCE_VOLUME|MUSIC_VOLUME|UI_VOLUME|VOICE_VOLUME) = .*/\1 = 0/' \
    "$ksp/settings.cfg" 2>/dev/null || true

# The craft is loaded in the editor it was built in. Which editor matters more
# than anything else here: the SPH defaults new craft to mirror symmetry and the
# VAB to radial, and B9 only builds a control surface end-for-end under mirror.
# Loading an SPH craft in the VAB quietly hides exactly the case worth looking at.
craftfile="$(find "$ksp/saves" -name "$craft.craft" -print -quit 2>/dev/null || true)"
if [[ "$craftfile" == */Ships/SPH/* ]]; then
    edit+=(-dstest-sph)
    echo "==> '$craft' is an SPH craft; starting in the Spaceplane Hangar"
fi

echo "==> loading '$craft'"
cd "$ksp"
: > KSP.log
LIBGL_ALWAYS_SOFTWARE=1 timeout 1500 \
    xvfb-run -n 99 -s "-screen 0 640x480x24" \
    ./KSP.x86_64 -force-glcore -dstest -dstest-inspect "$craft" "${edit[@]}" > run.out 2>&1 || true

echo
grep -E "\[DimensionSyncGameTests\]|\[DimensionSync\]" KSP.log | sed 's/^\[LOG [0-9:.]*\] //' || true
