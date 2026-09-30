#!/usr/bin/env bash
# Local release: next semver from commits since the last tag, Windows export, GitHub release.
# Usage: tools/release.sh [--dry-run | --after-push <sha>]   (Git Bash; --after-push is used by tools/hooks/pre-push)
# The build runs in a temporary worktree of the released commit, so your working files are never touched.
# Needs: gh (logged in), dotnet, Godot mono + export templates, 7z or PowerShell (for zipping).
set -euo pipefail
cd "$(dirname "$0")/.."
DRY=0; [ "${1:-}" = "--dry-run" ] && DRY=1
SHA=""; if [ "${1:-}" = "--after-push" ]; then SHA=$2; fi
GODOT=${GODOT:-'/c/ProgramData/chocolatey/lib/godot-mono/tools/godot_v4.7.1-stable_mono_win64/godot_v4.7.1-stable_mono_win64_console.exe'}
OUT=test_output/release; mkdir -p "$OUT"

if [ -n "$SHA" ]; then
  # the hook runs before the push lands: wait until origin/main is the pushed commit
  for _ in $(seq 60); do [ "$(git ls-remote origin refs/heads/main | cut -f1)" = "$SHA" ] && break; sleep 2; done
  [ "$(git ls-remote origin refs/heads/main | cut -f1)" = "$SHA" ] || { echo "push of $SHA never landed"; exit 1; }
  git fetch -q origin --tags
else
  [ "$(git branch --show-current)" = main ] || { echo "Not on main"; exit 1; }
  git fetch -q origin --tags
  [ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "main is not in sync with origin/main (push or pull first)"; exit 1; }
  SHA=$(git rev-parse HEAD)
fi

last=$(git describe --tags --abbrev=0 "$SHA" --match 'v[0-9]*' 2>/dev/null || true)
if [ -n "$last" ]; then range="$last..$SHA"; cur="${last#v}"; else range="$SHA"; cur="0.0.0"; fi
IFS=. read -r MA MI PA <<< "$cur"

# major: BREAKING in any commit; minor: anything not fix/docs/chore/merge; patch: only fixes/refactors; none: docs/chore only
git log --no-merges --format='%s' $range > "$OUT/subjects.txt"
if git log --no-merges --format='%B' $range | grep -qE 'BREAKING'; then bump=major
elif grep -qvE '^:(bug|ambulance|recycle|art|white_check_mark|memo|wrench|twisted_rightwards_arrows):' "$OUT/subjects.txt"; then bump=minor
elif grep -qE '^:(bug|ambulance|recycle|art|white_check_mark):' "$OUT/subjects.txt"; then bump=patch
else echo "Nothing releasable since ${last:-the start} (docs/chore only)."; exit 0; fi
case $bump in
  major) MA=$((MA+1)); MI=0; PA=0 ;;
  minor) MI=$((MI+1)); PA=0 ;;
  patch) PA=$((PA+1)) ;;
esac
V="$MA.$MI.$PA"

section() { # title, ERE on the leading emoji code
  local body; body=$(git log --no-merges --format='%s' $range | grep -E "$2" | sed -E 's/^:[a-z0-9_+-]+: *//; s/^/- /') || true
  if [ -n "$body" ]; then printf '### %s\n%s\n\n' "$1" "$body"; fi
}
{
  f=$(git log --no-merges --format='%s' $range | grep -vE '^:(bug|ambulance|memo|wrench|recycle|art|white_check_mark|boom):' | sed -E 's/^:[a-z0-9_+-]+: *//; s/^/- /') || true
  if [ -n "$f" ]; then printf '### Features and changes\n%s\n\n' "$f"; fi
  section "Fixes" '^:(bug|ambulance):'
  section "Docs and maintenance" '^:(memo|wrench|recycle|art|white_check_mark):'
  if [ -n "$last" ]; then echo "**Full diff:** $(git remote get-url origin | sed -E 's#git@github.com:#https://github.com/#; s#\.git$##')/compare/$last...v$V"; fi
} > "$OUT/notes.md"

echo "Next version: v$V ($bump)"; cat "$OUT/notes.md"
[ $DRY = 1 ] && exit 0

WT="$PWD/$OUT/wt"; REPO=$PWD
git worktree remove --force "$WT" 2>/dev/null || true
git worktree add -q --detach "$WT" "$SHA"
trap 'cd "$REPO"; git worktree remove --force "$WT"' EXIT
cd "$WT"
sed -i "s|^config/name=.*|&
config/version=\"$V\"|" project.godot

mkdir -p build/windows
dotnet build UnitSportSwitzerland.csproj -c Release
"$GODOT" --headless --path . --import || true
"$GODOT" --headless --path . --export-release "Windows Desktop" build/windows/UnitSportSwitzerland.exe
[ -f build/windows/UnitSportSwitzerland.exe ] || { echo "Export failed"; exit 1; }

ZIP="$REPO/$OUT/UnitSportSwitzerland-v$V-windows.zip"; rm -f "$ZIP"
powershell -NoProfile -Command "Compress-Archive -Path 'build/windows/*' -DestinationPath '$ZIP'"

gh release create "v$V" "$ZIP" --target "$SHA" --title "v$V" --notes-file "$OUT/notes.md"
echo "Released v$V"
