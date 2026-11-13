# Shared by the NAS runners (tools/build-france.sh, tools/build-swiss-nas.sh); source it, do not run it.
# The caller sets NAS, NAS_ROOT, BIN_DIR, PIDFILE, PUBLISH and HEAP_GB before calling anything here.

nas() { ssh -o BatchMode=yes -o ConnectTimeout=15 "$NAS" "$@"; }

# nas_config: the NAS settings from tools/deploy.env (copy tools/deploy.env.example), called before the defaults.
# Only NAS, NAS_ROOT, NAS_HEAP_GB (-> HEAP_GB) and NAS_JOBS (-> JOBS) are taken, read in a subshell: the rest of the
# file is the game server's (its TILES=1 would be the cube runner's tile list). Variables set in the environment win.
nas_config() {
  local f=tools/deploy.env line
  [ -f "$f" ] || return 0
  while IFS= read -r line; do
    case $line in
      NAS=*)         [ -n "${NAS+x}" ]      || NAS=${line#*=} ;;
      NAS_ROOT=*)    [ -n "${NAS_ROOT+x}" ] || NAS_ROOT=${line#*=} ;;
      NAS_HEAP_GB=*) [ -n "${HEAP_GB+x}" ]  || HEAP_GB=${line#*=} ;;
      NAS_JOBS=*)    [ -n "${JOBS+x}" ]     || JOBS=${line#*=} ;;
    esac
  done < <(set +eu; . "$f" > /dev/null 2>&1
           for v in NAS NAS_ROOT NAS_HEAP_GB NAS_JOBS; do [ -n "${!v-}" ] && printf '%s=%s\n' "$v" "${!v}"; done)
}

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
