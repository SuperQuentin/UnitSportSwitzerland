#!/usr/bin/env bash
# Downloads the vocal separation model CD analysis uses to find the singer (#1153) into the game's
# user data (user://models/), where no deploy or update touches it. Never shipped: the weights carry
# no clear redistribution licence, so each install fetches them itself. Without it the game falls
# back to a centre-panned mask (VocalSeparator).
# Usage: tools/deploy/get-vocal-model.sh [user data dir]   (default: this OS's Godot app_userdata)
# Run by tools/deploy/remote-setup.sh on the server as root with TARGET_USER set.
set -euo pipefail
NAME=Kim_Vocal_2.onnx
URL=https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/$NAME
SHA=ce74ef3b6a6024ce44211a07be9cf8bc6d87728cc852a68ab34eb8e58cde9c8b

if [ $# -ge 1 ]; then data=$1
elif [ -n "${APPDATA:-}" ]; then data="$APPDATA/Godot/app_userdata/UnitSportSwitzerland"
elif [ "$(uname)" = Darwin ]; then data="$HOME/Library/Application Support/Godot/app_userdata/UnitSportSwitzerland"
else data="${XDG_DATA_HOME:-$HOME/.local/share}/godot/app_userdata/UnitSportSwitzerland"
fi
dest="$data/models/$NAME"

sum() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1 | tr -d '\\'; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

if [ -f "$dest" ] && [ "$(sum "$dest")" = "$SHA" ]; then echo "vocal model: OK $dest"; exit 0; fi
mkdir -p "$data/models"
curl -fsSL --retry 3 -o "$dest.part" "$URL"
got=$(sum "$dest.part")
if [ "$got" != "$SHA" ]; then rm -f "$dest.part"; echo "vocal model: FAIL hash $got, expected $SHA" >&2; exit 1; fi
mv -f "$dest.part" "$dest"
echo "vocal model: INSTALLED $dest"
