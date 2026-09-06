#!/usr/bin/env bash
# Build the mod and the in-game harness, run KSP headless, print the report.
#
#   ./run_game_tests.sh                 # Xvfb, software GL
#   DS_DISPLAY=:0 ./run_game_tests.sh   # your own X display, hardware GL (much faster)
#   DS_KEEP_OPEN=1 ./run_game_tests.sh  # leave KSP running afterwards
#   DS_ONLY=a,b ./run_game_tests.sh     # only scenarios matching these substrings
#   DS_KSP_DIR=/path/to/KSP ./run_game_tests.sh   # use another install, for isolation
#
# Two sessions sharing one install destroy each other's work: this script deletes
# the report and KSP.log on the way in, both games write the same files, and on a
# shared Xvfb display synthetic input goes to whichever window has focus. That has
# already produced a "failure" that was really two games fighting. Copy the
# install (it is under 200 MB) and point DS_KSP_DIR at it.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
ksp="${DS_KSP_DIR:-$root/testenv/KSP}"
results="$ksp/dstest-results.json"
# Long enough for the whole suite, which now types into fields and drives the
# pointer and takes about half an hour. The old default of 600 s silently killed
# the run part-way through and left no report, which reads exactly like a run
# that found nothing.
timeout_s="${DS_TIMEOUT:-2700}"

# One game per install. Without this a second run deletes the first's report and
# log before failing to start, and the first run's results vanish with them.
if pgrep -af "KSP.x86_64" | grep -Fq -- "$ksp/dstest-results.json"; then
    echo "a KSP is already running against $ksp" >&2
    echo "wait for it, or copy the install and set DS_KSP_DIR to isolate this run" >&2
    exit 3
fi

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

# The test plugin as well. Its project writes straight into the DEFAULT install -
# KSPBT_ModRoot in the csproj is a fixed path - so an install selected with
# DS_KSP_DIR gets whatever test code happened to be there when it was copied.
# That silently ran hours-old scenarios against a freshly built mod, and the
# giveaway was a skip message whose wording had been changed and rebuilt.
tests_built="$root/testenv/KSP/GameData/DimensionSyncGameTests"
tests_here="$ksp/GameData/DimensionSyncGameTests"
if [[ -d "$tests_built" && "$tests_built" -ef "$tests_here" ]]; then
    :   # the default install: the build already wrote there
elif [[ -d "$tests_built" ]]; then
    mkdir -p "$tests_here"
    cp -a "$tests_built/." "$tests_here/"
fi
# Always refresh the config, then turn debug back on. Copying it only when
# absent would let a stale copy mask a change to the shipped defaults.
for f in DimensionSync.cfg changelog.cfg DimensionSync.version; do
    [[ -f "$root/GameData/DimensionSync/$f" ]] && cp "$root/GameData/DimensionSync/$f" "$ksp/GameData/DimensionSync/"
done
sed -i 's/^\(\s*\)debug = false/\1debug = true/' "$ksp/GameData/DimensionSync/DimensionSync.cfg"

rm -f "$results" "$ksp/KSP.log"

# KSP eats PartDatabase.cfg while loading and only rewrites it on a clean exit.
# Without it every part's drag cubes get re-rendered, which on software GL turns
# a 40-second startup into ten minutes.
seed="$root/testenv/PartDatabase.seed.cfg"
[[ -f "$seed" ]] && cp "$seed" "$ksp/PartDatabase.cfg"

# -dstest-sph runs the whole suite in the Spaceplane Hangar instead of the VAB.
# Pass --sph to this script to switch.
extra=()
[[ "${1:-}" == "--sph" ]] && extra+=(-dstest-sph)

args=(-dstest -dstest-out "$results")
# DS_ONLY=substring narrows the run to matching scenarios, for iterating on one.
[[ -n "${DS_ONLY:-}" ]] && args+=(-dstest-only "$DS_ONLY")
[[ -n "${DS_KEEP_OPEN:-}" ]] && args+=(-dstest-keep-open)

# Mute the install before every automated launch. SDL_AUDIODRIVER has no effect
# here - KSP uses FMOD on Linux, not SDL - so the volume settings are the only
# thing that actually silences it, and KSP rewrites them on exit, which means a
# session where somebody turned the sound up leaves it up for the next run.
sed -i -E 's/^(MASTER_VOLUME|SHIP_VOLUME|AMBIENCE_VOLUME|MUSIC_VOLUME|UI_VOLUME|VOICE_VOLUME) = .*/\1 = 0/' \
    "$ksp/settings.cfg" 2>/dev/null || true

# Read the window size out of KSP's own settings rather than guessing, with a
# margin so nothing sits flush against the edge.
screen_w=$(( $(grep -oP 'SCREEN_RESOLUTION_WIDTH\s*=\s*\K[0-9]+' "$ksp/settings.cfg" 2>/dev/null || echo 1024) + 64 ))
screen_h=$(( $(grep -oP 'SCREEN_RESOLUTION_HEIGHT\s*=\s*\K[0-9]+' "$ksp/settings.cfg" 2>/dev/null || echo 768) + 64 ))
echo "==> display ${screen_w}x${screen_h} for a $(grep -oP 'SCREEN_RESOLUTION_WIDTH\s*=\s*\K[0-9]+' "$ksp/settings.cfg" 2>/dev/null)x$(grep -oP 'SCREEN_RESOLUTION_HEIGHT\s*=\s*\K[0-9]+' "$ksp/settings.cfg" 2>/dev/null) window"

echo "==> running KSP (timeout ${timeout_s}s)"
cd "$ksp"
if [[ -n "${DS_DISPLAY:-}" ]]; then
    DISPLAY="$DS_DISPLAY" timeout "$timeout_s" ./KSP.x86_64 "${args[@]}" "${extra[@]}" > run.out 2>&1 || true
else
    # SDL_AUDIODRIVER=dummy so a headless run does not open the sound device at
    # all - the settings.cfg volumes are already zero, but a background run should
    # not be taking the machine's audio away from whoever is using it either.
    # -a picks a free display number rather than insisting on :99. A fixed number
    # is shared with every other session on the machine, and attaching to somebody
    # else's leftover server also means inheriting ITS resolution - which is how
    # runs asking for 640x480 ended up reporting 1024x768 and 1280x800.
    #
    # The screen has to be at least as big as KSP's own window, or the bottom and
    # right of that window hang off the display and the pointer cannot reach them:
    # the scenarios that type into the part action window aim at a "#" toggle near
    # the top right, and xdotool clamps to the screen, landing tens of pixels short.
    # This asked for 640x480 for a 1024x768 window and got away with it only
    # because it was never creating the server it asked for.
    # DS_OWNS_DISPLAY tells the synthetic-input helpers that this display was made
    # here and is safe to drive. They cannot tell that from the number, and must
    # never grab the pointer on a display somebody is actually using.
    LIBGL_ALWAYS_SOFTWARE=1 SDL_AUDIODRIVER=dummy DS_OWNS_DISPLAY=1 timeout "$timeout_s" \
        xvfb-run -a -s "-screen 0 ${screen_w}x${screen_h}x24" \
        ./KSP.x86_64 -force-glcore "${args[@]}" "${extra[@]}" > run.out 2>&1 || status=$?
fi
: "${status:=0}"

# Say WHICH failure this is. A run that was killed part-way and a run that
# crashed on startup both leave no report, and both used to print the same thing -
# after which a caller counting passes reports "0 passed, 0 failed, 0 skipped",
# which reads like a clean empty suite rather than like nothing having run.
if [[ "$status" -eq 124 ]]; then
    echo "TIMED OUT after ${timeout_s}s - the suite did not finish, so there is no report." >&2
    echo "Raise DS_TIMEOUT. Do not read this as a passing run." >&2
    tail -20 KSP.log >&2 || true
    exit 124
fi

if [[ ! -f "$results" ]]; then
    echo "NO REPORT PRODUCED (KSP exited $status); tail of KSP.log:" >&2
    tail -40 KSP.log >&2 || true
    exit 1
fi

# Print the report and exit non-zero if anything failed. Skips are not failures:
# they mean a mod this scenario needs is not installed.
python3 - "$results" <<'PY'
import json, sys
report = json.load(open(sys.argv[1]))
bad = 0
for s in report["scenarios"]:
    print(f"{s['status']:8} {s['name']}")
    for m in s["messages"]:
        print(f"         {m}")
    if s["status"] in ("failed", "error"):
        bad += 1
sys.exit(1 if bad else 0)
PY
