#!/usr/bin/env bash
# Build testenv/KSP: a throwaway KSP install holding only what the integration
# tests need. Everything large is symlinked out of your real install, so this
# costs a few hundred MB rather than tens of gigabytes, and nothing the tests do
# can touch your saves or your mods.
#
#   KSP_ROOT=/path/to/KSP ./setup_testenv.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
src="${KSP_ROOT:-$HOME/Games/Steam/steamapps/common/Kerbal Space Program}"
dst="$root/testenv/KSP"

[[ -f "$src/KSP.x86_64" ]] || { echo "no KSP install at '$src'; set KSP_ROOT" >&2; exit 2; }

echo "==> source install: $src"
rm -rf "$dst"
mkdir -p "$dst"/{GameData,saves,Logs,PluginData,thumbs,Screenshots,Ships/VAB,Ships/SPH}

# The executable and the native runtime must be real files: Unity looks for
# KSP_Data next to the binary it actually resolves.
cp "$src/KSP.x86_64" "$src/UnityPlayer.so" "$dst/"
cp "$src/Physics.cfg" "$dst/"
[[ -f "$src/buildID.txt" ]] && cp "$src/buildID.txt" "$dst/"

# A primed part database is what keeps startup to well under a minute: without
# it KSP re-renders every part's drag cubes, which on a software GL stack takes
# many minutes. KSP consumes the file as it loads and only writes a fresh one on
# a clean exit, so keep a seed copy to restore before every run.
if [[ -f "$src/PartDatabase.cfg" ]]; then
    cp "$src/PartDatabase.cfg" "$root/testenv/PartDatabase.seed.cfg"
    cp "$src/PartDatabase.cfg" "$dst/PartDatabase.cfg"
fi

# KSP_Data must be a real directory, not a symlink: KSP builds paths like
# "KSP_Data/../saves", and the kernel resolves ".." through a symlink to the
# *target's* parent - which would send saves, craft files and PartDatabase.cfg
# into the real install. Mirror the tree instead: real directories, symlinked
# files. Costs a few hundred inodes and no disk.
# mirror_tree SRC DST
#
# Recreate SRC's directory structure under DST as real directories, and every
# file in it as a symlink back to the original. Costs a few hundred inodes and
# no disk, while keeping ".." relative to DST rather than to SRC.
mirror_tree() {
    local from="$1" to="$2"
    ( cd "$from" && find . -mindepth 1 -type d -printf '%P\0' ) | ( cd "$to" && xargs -0 --no-run-if-empty mkdir -p )
    # Null-delimited, because KSP's asset names contain spaces and quotes.
    ( cd "$from" && find . -mindepth 1 ! -type d -printf '%P\0' ) | while IFS= read -r -d '' f; do
        ln -sfn "$from/$f" "$to/$f"
    done
}

# The engine's own data directories. Mirrored rather than symlinked for the
# reason above; the rest are tiny and mirrored only for consistency.
for d in KSP_Data KSP_x64_Data Parts Internals Resources sounds Plugins Missions; do
    [[ -e "$src/$d" ]] || continue
    mkdir -p "$dst/$d"
    mirror_tree "$src/$d" "$dst/$d"
done
# Unity rewrites boot.config on some launches; keep that off the real install.
[[ -e "$dst/KSP_Data/boot.config" ]] && cp --remove-destination "$src/KSP_Data/boot.config" "$dst/KSP_Data/boot.config"

# --- GameData -------------------------------------------------------------
# Squad, minus the parts and assets the tests never look at.
# Squad ships ~360 parts and 190 MB of IVA spaces; the tests need a handful.
# Everything not linked here is simply absent, which is most of the reason a
# run starts in seconds rather than minutes.
mkdir -p "$dst/GameData/Squad/Parts"/{Command,Coupling,FuelTank}
for d in Agencies Contracts Controls Experience Flags FlagsAgency FlagsOrganization \
         FX Interiors Localization MenuProps PartList Plugins Props Resources Sounds Strategies; do
    [[ -e "$src/GameData/Squad/$d" ]] && ln -sfn "$src/GameData/Squad/$d" "$dst/GameData/Squad/$d"
done
for f in squadcore.ksp squadcorefx.ksp; do
    [[ -e "$src/GameData/Squad/$f" ]] && ln -sfn "$src/GameData/Squad/$f" "$dst/GameData/Squad/$f"
done
cp "$src/GameData/Squad/Parts/VariantThemes.cfg" "$dst/GameData/Squad/Parts/" 2>/dev/null || true
ln -sfn "$src/GameData/Squad/Parts/Resources" "$dst/GameData/Squad/Parts/Resources"
ln -sfn "$src/GameData/Squad/Parts/Command/probeCoreOcto_v2" "$dst/GameData/Squad/Parts/Command/probeCoreOcto_v2"
ln -sfn "$src/GameData/Squad/Parts/Coupling/Assets" "$dst/GameData/Squad/Parts/Coupling/Assets"
cp "$src/GameData/Squad/Parts/Coupling/Decoupler_1.cfg" "$dst/GameData/Squad/Parts/Coupling/" 2>/dev/null || true
ln -sfn "$src/GameData/Squad/Parts/FuelTank/Size1_Tanks" "$dst/GameData/Squad/Parts/FuelTank/Size1_Tanks"

# ModuleManager, whichever version is installed.
for mm in "$src/GameData"/ModuleManager*.dll; do
    [[ -e "$mm" ]] && ln -sfn "$mm" "$dst/GameData/$(basename "$mm")"
done

# The mods under test, plus the shared libraries they declare KSPAssembly
# dependencies on. Miss one of those and KSP silently refuses to load the
# dependent assembly, so its parts compile with no PartModules at all.
for m in ProceduralParts ROLib ProceduralFairings B9_Aerospace_ProceduralWings \
         ROUtils Shabby 000_Harmony 000_TexturesUnlimited KSPCommunityFixes; do
    [[ -d "$src/GameData/$m" ]] && cp -r "$src/GameData/$m" "$dst/GameData/$m"
done
for m in ROTanks; do
    [[ -d "$src/GameData/$m" ]] && ln -sfn "$src/GameData/$m" "$dst/GameData/$m"
done

# DimensionSync itself, with verbose logging turned on.
mkdir -p "$dst/GameData/DimensionSync"
cp "$root/GameData/DimensionSync/DimensionSync.cfg" "$dst/GameData/DimensionSync/" 2>/dev/null || true
sed -i 's/^\(\s*\)debug = false/\1debug = true/' "$dst/GameData/DimensionSync/DimensionSync.cfg" 2>/dev/null || true

# Windowed and small, so a run on a real display stays out of the way.
if [[ -f "$src/settings.cfg" ]]; then
    # Also silence the tutorial and expansion splashes: on a fresh save they open
    # on top of the walkthrough panel and a scripted run cannot click them away.
    #
    # The volumes are left alone: automated runs pass SDL_AUDIODRIVER=dummy, so
    # they are silent whatever this file says, and anyone launching the install to
    # actually play it gets sound.
    sed -e 's/^SCREEN_RESOLUTION_WIDTH = .*/SCREEN_RESOLUTION_WIDTH = 1024/' \
        -e 's/^SCREEN_RESOLUTION_HEIGHT = .*/SCREEN_RESOLUTION_HEIGHT = 768/' \
        -e 's/^FULLSCREEN = .*/FULLSCREEN = False/' \
        -e 's/^TUTORIALS_EDITOR_ENABLE = .*/TUTORIALS_EDITOR_ENABLE = False/' \
        -e 's/^TUTORIALS_FLIGHT_ENABLE = .*/TUTORIALS_FLIGHT_ENABLE = False/' \
        -e 's/^SHOW_WHATSNEW_DIALOG = .*/SHOW_WHATSNEW_DIALOG = False/' \
        -e 's/^MISSION_SHOW_EXPANSION_INFO = .*/MISSION_SHOW_EXPANSION_INFO = False/' \
        -e 's/^SERENITY_SHOW_EXPANSION_INFO = .*/SERENITY_SHOW_EXPANSION_INFO = False/' \
        "$src/settings.cfg" > "$dst/settings.cfg"
fi

echo "==> testenv ready at $dst"
du -sh "$dst" 2>/dev/null || true
