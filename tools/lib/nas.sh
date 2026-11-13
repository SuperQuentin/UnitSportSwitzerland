# Shared by the NAS runners (tools/build-france.sh, tools/build-swiss-nas.sh); source it, do not run it.
# The caller sets NAS, NAS_ROOT, BIN_DIR, PIDFILE, PUBLISH and HEAP_GB before calling anything here.

nas() { ssh -o BatchMode=yes -o ConnectTimeout=15 "$NAS" "$@"; }

# TerrainPreprocessor, self-contained linux-x64 (own .NET runtime: nothing installed on the NAS, no root)
publish() {
  echo "== dotnet publish linux-x64 (self-contained)"
  dotnet publish tools/TerrainPreprocessor/TerrainPreprocessor.csproj -c Release -r linux-x64 --self-contained \
    -p:DebugType=none -o "$PUBLISH" -v q -nologo
  local size; size=$(du -sh "$PUBLISH" | cut -f1)
  echo "== copy $size to $NAS:$BIN_DIR"
  # tar over ssh: one stream, not thousands of scp round trips; the old files stay, the new ones overwrite
  nas "mkdir -p '$BIN_DIR'"
  (cd "$PUBLISH" && tar -cf - *) | nas "tar -C '$BIN_DIR' --no-overwrite-dir --no-same-owner --no-same-permissions -m -xf - && chmod +x '$BIN_DIR/TerrainPreprocessor' 2>/dev/null; test -x '$BIN_DIR/TerrainPreprocessor'"
  echo "== published"
}

alive() { nas "test -f '$PIDFILE' && kill -0 \$(cat '$PIDFILE') 2>/dev/null"; }

heap_hex() { printf '0x%x' $((HEAP_GB * 1024 * 1024 * 1024)); }
