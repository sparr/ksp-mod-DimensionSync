#!/usr/bin/env bash
# Build the releasable GameData folder and zip it, refusing if anything about the
# release disagrees with itself.
#
#   ./make-release.sh                 # check, build, zip into dist/
#   ./make-release.sh --allow-dirty   # ...from a working tree with uncommitted changes
#   ./make-release.sh --with-pdb      # include the debug symbols in the zip
#
# The checks are the point. Every one of them is a mistake that has actually been
# made here or come within one step of shipping: a version typed into the in-game
# changelog and nowhere else, a shipped config left in debugging mode, a readme
# describing a default the config does not have. None of them produce an error at
# build time and none of them are visible in the zip without opening it.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

allow_dirty=""
with_pdb=""
for arg in "$@"; do
    case "$arg" in
        --allow-dirty) allow_dirty=1 ;;
        --with-pdb)    with_pdb=1 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

fail() { echo "RELEASE CHECK FAILED: $*" >&2; exit 1; }

# --- 1. the tree ----------------------------------------------------------
# A release should be rebuildable from a commit. Uncommitted changes mean the
# zip contains something no revision describes, which is only discovered later
# when nobody can reproduce the artifact.
if [[ -z "$allow_dirty" ]] && [[ -n "$(git status --porcelain)" ]]; then
    git status --short >&2
    fail "uncommitted changes. Commit them, or pass --allow-dirty for a test build."
fi

# --- 2. the version, from the one place it is typed ------------------------
version="$(grep -oP '(?<=<Version>)[^<]+' src/DimensionSync.csproj || true)"
[[ -n "$version" ]] || fail "no <Version> in src/DimensionSync.csproj"
echo "==> version $version"

# --- 3. build, which regenerates everything else ---------------------------
echo "==> building"
if ! dotnet build src/DimensionSync.csproj -v quiet --nologo; then
    fail "the mod did not compile."
fi

mod="GameData/DimensionSync"

# --- 4. the version agrees everywhere -------------------------------------
# <Version> reaches the DLL and the .version file on its own. changelog.cfg is
# hand-written and reaches nothing, so it is the one that drifts: an in-game
# changelog headed 0.2.0 beside a .version claiming 0.3.0 looks right in both
# files and only disagrees when you read them together.
avc="$(grep -oP '(?<="VERSION": ")[^"]+' "$mod/DimensionSync.version" || true)"
[[ "$avc" == "$version" || "$avc" == "$version.0" ]] \
    || fail "DimensionSync.version says $avc, csproj says $version"

grep -qE "^[[:space:]]*version[[:space:]]*=[[:space:]]*$version(\.0)?[[:space:]]*$" "$mod/changelog.cfg" \
    || fail "changelog.cfg has no VERSION block for $version (it is hand-written; add one)"

grep -qF "## [$version]" CHANGELOG.md \
    || fail "CHANGELOG.md has no '## [$version]' section"

# --- 5. the shipped config is the one described ---------------------------
# debug shipped as true for the whole of 0.1.x, against a readme documenting
# false, so every install logged several lines per frame of editing.
grep -qE "^[[:space:]]*debug[[:space:]]*=[[:space:]]*false" "$mod/DimensionSync.cfg" \
    || fail "$mod/DimensionSync.cfg does not have debug = false"

# Every default the readme's settings table states must be what the config
# actually ships, which is the check that would have caught debug on its own.
while IFS='|' read -r _ key value _; do
    key="$(echo "$key" | tr -d ' `')"
    value="$(echo "$value" | tr -d ' `')"
    [[ -n "$key" && -n "$value" ]] || continue
    grep -qE "^[[:space:]]*$key[[:space:]]*=[[:space:]]*$value[[:space:]]*$" "$mod/DimensionSync.cfg" \
        || fail "readme documents $key = $value, which is not what DimensionSync.cfg ships"
done < <(sed -n '/^| `debug`/,/^$/p' README.md)

# --- 5b. the update URL points at a branch that exists --------------------
# KSP-AVC and CKAN both read this URL to find out whether an update exists, and
# it is the one field in the .version file the build does NOT rewrite, so it
# keeps whatever it was given. It said "master" while the only branch was
# "main": reachable through GitHub's alias, and reported 404 by CKAN's own
# inflater, which is a good reminder that a machine-read URL should not depend
# on an alias resolving.
avc_url="$(grep -oP '(?<="URL": ")[^"]+' "$mod/DimensionSync.version" || true)"
if [[ -n "$avc_url" ]]; then
    avc_branch="$(sed -E 's#.*githubusercontent\.com/[^/]+/[^/]+/([^/]+)/.*#\1#' <<<"$avc_url")"
    if [[ -n "$avc_branch" && "$avc_branch" != "$avc_url" ]]; then
        git rev-parse --verify --quiet "refs/heads/$avc_branch" >/dev/null \
            || fail "the .version URL points at branch '$avc_branch', which does not exist here"
    fi
fi

# --- 6. the build actually copied the docs --------------------------------
# SkipUnchangedFiles means a copy that quietly did not happen leaves the last
# release's readme in the folder, which is not visible in the build output.
for f in LICENSE README.md CHANGELOG.md; do
    cmp -s "$f" "$mod/$f" || fail "$mod/$f differs from the repository's $f"
done

[[ -f "$mod/Plugins/DimensionSync.dll" ]] || fail "no DLL in $mod/Plugins"

# --- 7. the zip -----------------------------------------------------------
# GameData at the top level, so unzipping over a KSP install is the whole
# installation procedure.
mkdir -p dist
zip_path="dist/DimensionSync-$version.zip"
rm -f "$zip_path"
if [[ -n "$with_pdb" ]]; then
    zip -qr "$zip_path" GameData
else
    zip -qr "$zip_path" GameData -x "*.pdb"
fi

echo
echo "==> $zip_path"
unzip -l "$zip_path" | sed '1,3d;$d'
echo
echo "Next, once the suites are green:"
echo "    git tag -a v$version -m 'DimensionSync $version'"
echo "    git push origin main --follow-tags"
echo "    gh release create v$version $zip_path --title 'DimensionSync $version' --notes-file <notes>"
