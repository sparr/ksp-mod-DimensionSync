#!/usr/bin/env bash
# Build the mod and the in-game harness, run KSP headless, print the report.
#
#   ./run_game_tests.sh                 # Xvfb, software GL
#   DS_DISPLAY=:0 ./run_game_tests.sh   # your own X display, hardware GL (much faster)
#   DS_KEEP_OPEN=1 ./run_game_tests.sh  # leave KSP running afterwards
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
ksp="$root/testenv/KSP"
results="$ksp/dstest-results.json"
timeout_s="${DS_TIMEOUT:-600}"

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
[[ -n "${DS_KEEP_OPEN:-}" ]] && args+=(-dstest-keep-open)

# Mute the install before every automated launch. SDL_AUDIODRIVER has no effect
# here - KSP uses FMOD on Linux, not SDL - so the volume settings are the only
# thing that actually silences it, and KSP rewrites them on exit, which means a
# session where somebody turned the sound up leaves it up for the next run.
sed -i -E 's/^(MASTER_VOLUME|SHIP_VOLUME|AMBIENCE_VOLUME|MUSIC_VOLUME|UI_VOLUME|VOICE_VOLUME) = .*/\1 = 0/' \
    "$ksp/settings.cfg" 2>/dev/null || true

echo "==> running KSP (timeout ${timeout_s}s)"
cd "$ksp"
if [[ -n "${DS_DISPLAY:-}" ]]; then
    DISPLAY="$DS_DISPLAY" timeout "$timeout_s" ./KSP.x86_64 "${args[@]}" "${extra[@]}" > run.out 2>&1 || true
else
    # SDL_AUDIODRIVER=dummy so a headless run does not open the sound device at
    # all - the settings.cfg volumes are already zero, but a background run should
    # not be taking the machine's audio away from whoever is using it either.
    LIBGL_ALWAYS_SOFTWARE=1 SDL_AUDIODRIVER=dummy timeout "$timeout_s" \
        xvfb-run -n 99 -s "-screen 0 640x480x24" \
        ./KSP.x86_64 -force-glcore "${args[@]}" "${extra[@]}" > run.out 2>&1 || true
fi

if [[ ! -f "$results" ]]; then
    echo "no report produced; tail of KSP.log:" >&2
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
