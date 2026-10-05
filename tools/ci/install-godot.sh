#!/usr/bin/env bash
# Install Godot mono and its export templates on a CI runner. Used by .github/workflows/release.yml.
# Env: GODOT_VERSION (4.7.1-stable), GODOT (where to put the binary: <dir>/godot).
# The templates must land in <version>.<status>.mono ("4.7.1.stable.mono"), the name Godot looks for;
# anything else and every export silently produces nothing.
set -euo pipefail
GODOT_VERSION=${GODOT_VERSION:-4.7.1-stable}
GODOT=${GODOT:?set GODOT to the path the binary should have}
base=https://github.com/godotengine/godot-builds/releases/download/$GODOT_VERSION
templates="$HOME/.local/share/godot/export_templates/${GODOT_VERSION/-/.}.mono"

mkdir -p "$(dirname "$GODOT")" "$(dirname "$templates")"

# Editor (~100 MB). The zip may or may not wrap everything in a folder, so find the binary itself;
# GodotSharp/ has to stay beside it, which is why the whole directory is copied.
curl -fsSL -o /tmp/godot.zip "$base/Godot_v${GODOT_VERSION}_mono_linux_x86_64.zip"
unzip -q /tmp/godot.zip -d /tmp/godot
bin=$(find /tmp/godot -type f -name 'Godot_v*_mono_linux.x86_64' | head -1)
[ -n "$bin" ] || { echo "No Godot binary in $base/Godot_v${GODOT_VERSION}_mono_linux_x86_64.zip"; ls -R /tmp/godot | head -30; exit 1; }
cp -r "$(dirname "$bin")/." "$(dirname "$GODOT")/"
mv "$(dirname "$GODOT")/$(basename "$bin")" "$GODOT"
chmod +x "$GODOT"

# Export templates (~1.15 GB): the tpz is a zip holding a single templates/ directory.
curl -fsSL -o /tmp/templates.tpz "$base/Godot_v${GODOT_VERSION}_mono_export_templates.tpz"
unzip -q /tmp/templates.tpz -d /tmp/tpz
rm -rf "$templates"; mv /tmp/tpz/templates "$templates"
rm -f /tmp/godot.zip /tmp/templates.tpz

echo "Godot:     $("$GODOT" --headless --version)"
echo "Templates: $templates ($(ls "$templates" | wc -l | tr -d ' ') files, version.txt: $(cat "$templates/version.txt" 2>/dev/null || echo MISSING))"
[ -s "$templates/version.txt" ] || { echo "No version.txt in the templates: Godot will not find them"; exit 1; }
