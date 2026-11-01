#!/usr/bin/env bash
# NAS side of tools/build-swiss-nas.sh: every stage of the whole-Switzerland rebuild in tile format v3, in one
# process, so it goes on while nobody is connected. Copied to the NAS and run by `bash` (the NAS login shell is zsh).
# Env (all set by the caller): OUT TEMP DATA BIN JOBS NICE FR_BOX OTHER_BOX IN_DIR DEM_CACHE_MB; optional FROM,
# FR_DEPTS, NO_HASH. A finished stage leaves $TEMP/stage_<name>.done and is skipped on the next start; FROM=<stage>
# redoes that stage and every later one.
set -uo pipefail
STAGES=(terrain frdem dem osm features airports hash)
: "${IN_DIR:=$DATA/swiss_chunks}" "${DEM_CACHE_MB:=4096}"
mkdir -p "$OUT" "$TEMP"
export DOTNET_TieredPGO=0 DOTNET_GCConserveMemory=5
now() { date '+%F %T'; }
say() { echo "=== $(now) $*"; }

if [ -n "${FROM:-}" ]; then
  hit=0
  for s in "${STAGES[@]}"; do
    [ "$s" = "$FROM" ] && hit=1
    [ $hit = 1 ] && rm -f "$TEMP/stage_$s.done"
  done
  [ $hit = 1 ] || { say "FROM=$FROM is not a stage (${STAGES[*]})"; exit 2; }
fi

child=0
trap '[ $child != 0 ] && kill -TERM $child 2>/dev/null; wait; say "stopped (SIGTERM)"; exit 143' TERM INT

# stage <name> <args...>: the preprocessor with the common flags, timed, with its peak RSS
stage() {
  local name=$1; shift
  if [ -f "$TEMP/stage_$name.done" ]; then say "stage $name: done earlier, skipped"; return 0; fi
  say "stage $name: start"
  local t0=$SECONDS hwm=0 v rc
  nice -n "${NICE:-10}" ionice -c2 -n7 "$BIN/TerrainPreprocessor" "$@" --out "$OUT" --temp "$TEMP" --jobs "$JOBS" \
    --height-format wide ${HASH_FLAG---no-hash} < /dev/null &
  child=$!
  while kill -0 $child 2>/dev/null; do
    v=$(awk '/VmHWM/ {print $2}' /proc/$child/status 2>/dev/null || true)
    [ -n "$v" ] && hwm=$v
    sleep 1
  done
  wait $child; rc=$?; child=0
  if [ $rc != 0 ]; then say "stage $name: FAILED (exit $rc) after $((SECONDS - t0)) s"; exit $rc; fi
  touch "$TEMP/stage_$name.done"
  say "stage $name: done in $((SECONDS - t0)) s, peak RSS $((hwm / 1024)) MB"
}

tlm=$(ls -t "$DATA"/tlm3d/*.gpkg 2>/dev/null | head -1)
pbf=$(ls -t "$DATA"/osm/switzerland-*.osm.pbf 2>/dev/null | head -1)
[ -n "$tlm" ] || { say "no swissTLM3D .gpkg in $DATA/tlm3d"; exit 1; }
[ -d "$IN_DIR" ] || { say "no swissALTI3D tiles in $IN_DIR"; exit 1; }

# 1. swissALTI3D -> .terr/.terrc, manifest, far horizon (wide heights)
stage terrain --in "$IN_DIR"

# 2. France: RGE ALTI over the box (Chablais and Geneva by default), Swiss border tiles parsed again over it
fr=(--dem "lambert93,$DATA/france/rgealti,-0.28")
[ -n "${FR_DEPTS:-}" ] && fr+=(--dem-only "$FR_DEPTS")
stage frdem --in "$IN_DIR" "${fr[@]}" --dem-box "$FR_BOX" --dem-reparse --dem-cache-mb "$DEM_CACHE_MB"

# 3. Germany (Baden-Wuerttemberg), Austria (Vorarlberg), Italy (South Tyrol): what was downloaded, dz as in #968
stage dem --in "$IN_DIR" \
  --dem "utm32,$DATA/germany_bw_dgm1,0.23" --dem "laea,$DATA/austria_bev_dtm,0.12" --dem "utm32,$DATA/italy_bz_dtm,0.08" \
  --dem-box "$OTHER_BOX" --dem-reparse --dem-cache-mb "$DEM_CACHE_MB"

# 4. OSM overlay (whole map)
if [ -n "$pbf" ]; then stage osm --tlm "$tlm" --osm-overlay "$pbf"; else say "stage osm: no OSM extract, skipped"; fi

# 5. the feature pass, as tools/rebuild-map.sh all: places, roads, buildings, road network, cover, trees, water, landings
args=(--features-only --tlm "$tlm" --cover)
[ -f "$DATA/routes/route_keys.sqlite" ] && args+=(--route-keys "$DATA/routes/route_keys.sqlite")
[ -d "$DATA/bathy3d" ] && args+=(--bathy "$DATA/bathy3d")
b=$DATA/buildings3d
if [ -f "$b/buildings_ch.gpkg" ]; then
  args+=(--buildings "$b/buildings_ch.gpkg")
else
  # the newest flight of each sheet
  while IFS= read -r zip; do args+=(--buildings-gdb "$zip"); done < <(
    ls "$b"/swissbuildings3d_3_0_*.gdb.zip 2>/dev/null | sort |
      awk '{ k = $0; sub(/.*swissbuildings3d_3_0_[0-9]+_/, "", k); last[k] = $0 } END { for (k in last) print last[k] }' | sort)
fi
[ -f "$DATA/gwr/data.sqlite" ] && args+=(--gwr "$DATA/gwr/data.sqlite" --places)
stage features "${args[@]}"

# 6. airports: stands and runway profiles from OSM, after the buildings
if [ -n "$pbf" ]; then stage airports --tlm "$tlm" --osm "$pbf" --airports; fi

# 7. content hashes of the manifest (#924): one pass at the end, local disk here (the passes above skip it)
if [ -z "${NO_HASH:-}" ]; then HASH_FLAG= stage hash --manifest-hash; fi
say "ALL STAGES DONE"
