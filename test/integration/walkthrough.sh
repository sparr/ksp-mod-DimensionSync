#!/usr/bin/env bash
# Step through the integration scenarios in a live KSP window, pausing before
# each operation to say what is about to happen and what should come of it.
#
#   DS_DISPLAY=:0 test/integration/walkthrough.sh   # on your desktop, to watch
#   test/integration/walkthrough.sh                 # on a private Xvfb display
#
# Without DS_DISPLAY it runs on its own Xvfb display, where it cannot take focus
# from anything else and input aimed at it cannot reach your other windows. That
# is the right mode for driving it from a script; software GL makes it slower but
# it is otherwise identical. Screenshot it with:
#
#   DISPLAY=:99 import -window root shot.png
#
# In the game: Space or Next advances a step. "Skip scenario" moves on, "Run the
# rest" finishes without pausing, "Stop" ends the run early. The game is left
# running when it finishes; quit it normally.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
ksp="$root/testenv/KSP"
results="$ksp/dstest-results.json"

if [[ ! -x "$ksp/KSP.x86_64" ]]; then
    echo "no test install at $ksp - run test/integration/setup_testenv.sh first" >&2
    exit 2
fi

echo "==> building"
dotnet build "$root/src/DimensionSync.csproj" -v quiet --nologo
dotnet build "$root/test/DimensionSync.GameTests/DimensionSync.GameTests.csproj" -v quiet --nologo

echo "==> deploying"
mkdir -p "$ksp/GameData/DimensionSync/Plugins"
cp "$root/GameData/DimensionSync/Plugins/DimensionSync.dll" "$ksp/GameData/DimensionSync/Plugins/"
# Always refresh the config, then turn debug back on. Copying it only when
# absent would let a stale copy mask a change to the shipped defaults.
for f in DimensionSync.cfg changelog.cfg DimensionSync.version; do
    [[ -f "$root/GameData/DimensionSync/$f" ]] && cp "$root/GameData/DimensionSync/$f" "$ksp/GameData/DimensionSync/"
done
sed -i 's/^\(\s*\)debug = false/\1debug = true/' "$ksp/GameData/DimensionSync/DimensionSync.cfg"

# The report is deliberately left in place: a walkthrough is usually abandoned
# part way through, and deleting it up front would leave pytest with no scenario
# names to collect from on the next headless run.
rm -f "$ksp/KSP.log"
seed="$root/testenv/PartDatabase.seed.cfg"
[[ -f "$seed" ]] && cp "$seed" "$ksp/PartDatabase.cfg"

echo "==> launching KSP; it will stop at the first scenario and wait for you"
# Mute the install before every automated launch. SDL_AUDIODRIVER has no effect
# here - KSP uses FMOD on Linux, not SDL - so the volume settings are the only
# thing that actually silences it, and KSP rewrites them on exit, which means a
# session where somebody turned the sound up leaves it up for the next run.
sed -i -E 's/^(MASTER_VOLUME|SHIP_VOLUME|AMBIENCE_VOLUME|MUSIC_VOLUME|UI_VOLUME|VOICE_VOLUME) = .*/\1 = 0/' \
    "$ksp/settings.cfg" 2>/dev/null || true

# --sph runs the walkthrough in the Spaceplane Hangar instead of the VAB. The two
# lay a craft out along different axes, so anything reasoning in world space
# rather than in a part's own space behaves differently between them.
extra=()
[[ "${1:-}" == "--sph" ]] && { extra+=(-dstest-sph); shift; }

# Anything else is a scenario filter, so a walkthrough can be about the handful of
# scenarios in question instead of every one of them:
#
#   test/integration/walkthrough.sh probe_
#   test/integration/walkthrough.sh probe_swept,probe_tapered
[[ -n "${1:-}" ]] && extra+=(-dstest-only "$1")

# A walkthrough is for WATCHING, so it opens on a display somebody is looking at
# unless told otherwise. It used to do the opposite: with DS_DISPLAY unset it went
# to the throwaway Xvfb display, printed everything a successful start prints, and
# showed nothing - which is indistinguishable from the game failing to open.
#
# DS_DISPLAY=headless when a guided run really is meant to be unwatched.
display="${DS_DISPLAY:-${DISPLAY:-:0}}"

cd "$ksp"
if [[ "$display" != "headless" ]]; then
    echo "==> opening on display $display (DS_DISPLAY=headless to run it unwatched)"
    # SDL_AUDIODRIVER=dummy so a guided run is silent whatever the install's own
    # volume settings happen to be. Keeping it here rather than in settings.cfg
    # leaves that shared file free to be whatever somebody playing the game wants.
    exec env DISPLAY="$display" SDL_AUDIODRIVER=dummy \
        ./KSP.x86_64 -dstest-walkthrough "${extra[@]}" -dstest-out "$results"
fi

echo "==> DS_DISPLAY=headless; running unwatched on Xvfb"
exec env LIBGL_ALWAYS_SOFTWARE=1 xvfb-run -n 99 -s "-screen 0 1280x800x24" \
     env SDL_AUDIODRIVER=dummy ./KSP.x86_64 -force-glcore -dstest-walkthrough "${extra[@]}" -dstest-out "$results"
