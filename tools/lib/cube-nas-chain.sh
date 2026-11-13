#!/usr/bin/env bash
# NAS side of `tools/build-swiss-nas.sh cube ...`: the earth leaves (cube-sphere L14 `.cubl` bundles and
# cube_manifest.json, #1794) of one region, every stage of MapSetup's `cube` layer (tools/MapCore/Pipeline.cs,
# AddCubeSteps) as headless TerrainPreprocessor modes, in one process, so it goes on while nobody is connected.
# Copied to the NAS and run by `bash` (the NAS login shell is zsh). No pyramid (`--cube-swiss`): a game server must
# not get cube_global (docs/notes/general/linux-deploy.md).
# Env (set by the caller): OUT (the region, <chunks>_cube) TEMP (<OUT>_temp) DATA BIN JOBS NICE BOX (LV95 metres
# minE,minN,maxE,maxN) TILES_FILE (the selection: LV95 km tiles, E-N a line) IN_DIR (where its swissALTI3D zips are
# linked) FRAMES; optional FROM, ALTI2M=1, RGEALTI=1, NO_OSM=1, CHECK_ONLY=1, MISSING_TILES_OK=1.
# A finished stage leaves $TEMP/stage_<name>.done and is skipped on the next start; FROM=<stage> redoes that stage
# and every later one. Before any stage runs, every source the stages still to run read is checked: a missing one
# stops the run with what to fetch, never an empty stage.
set -uo pipefail
STAGES=(heights roads buildings sources places network cover water covernet trees fields pistes files)
: "${FRAMES:=1}" "${NICE:=10}"
export DOTNET_TieredPGO=0 DOTNET_GCConserveMemory=5
now() { date '+%F %T'; }
say() { echo "=== $(now) $*"; }

if [ -n "${FROM:-}" ]; then
  hit=0
  for s in "${STAGES[@]}"; do [ "$s" = "$FROM" ] && hit=1; done
  [ $hit = 1 ] || { say "FROM=$FROM is not a stage (${STAGES[*]})"; exit 2; }
fi

# ---- the selection's swissALTI3D tiles ------------------------------------------------------------------------
# The heights stage reads every zip under --in, so only the selection's 0.5 m zips are linked into IN_DIR: the leaves
# stay in the region, as B4's data folder held only its 825 tiles. The newest flight of a tile wins (names sort by
# year); tiles with no zip go into IN_DIR.missing.
if [ -n "${TILES_FILE:-}" ]; then
  rm -rf "$IN_DIR"; mkdir -p "$IN_DIR"; : > "$IN_DIR.missing"
  declare -A want=() have=()
  while IFS= read -r t; do t=${t%%#*}; t=${t//[[:space:]]/}; [ -n "$t" ] && want[$t]=1; done < "$TILES_FILE"
  while IFS= read -r f; do
    t=${f#swissalti3d_*_}; t=${t%%_*}
    [ -n "${want[$t]:-}" ] && have[$t]=$f
  done < <(ls "$DATA/swiss_chunks" 2>/dev/null | grep -E '^swissalti3d_[0-9]+_[0-9]+-[0-9]+_0\.5_.*\.xyz\.zip$' | sort)
  for t in "${!have[@]}"; do ln -s "$DATA/swiss_chunks/${have[$t]}" "$IN_DIR/"; done
  for t in "${!want[@]}"; do [ -z "${have[$t]:-}" ] && echo "$t" >> "$IN_DIR.missing"; done
  sort -o "$IN_DIR.missing" "$IN_DIR.missing"
  say "selection: ${#want[@]} tiles, ${#have[@]} swissALTI3D zips linked into $IN_DIR"
fi

# ---- the sources (newest name wins, as MapCore's Paths) -------------------------------------------------------
tlm=$(ls "$DATA"/tlm3d/*.gpkg 2>/dev/null | sort | tail -1)
pbf=$(ls "$DATA"/osm/switzerland-*.osm.pbf 2>/dev/null | sort | tail -1)
[ -n "${NO_OSM:-}" ] && pbf=
gwr=$DATA/gwr/data.sqlite
bathy=$DATA/bathy3d
lwb=$DATA/lwb
alti2m=$DATA/swissalti3d_2m
rgealti=$DATA/france/rgealti
boundaries=$(ls "$DATA"/boundaries/swissboundaries3d_*.gpkg "$DATA"/swissboundaries3d_*.gpkg 2>/dev/null | sort | tail -1)
b=$DATA/buildings3d
buildings=()
if [ -f "$b/buildings_ch.gpkg" ]; then
  buildings=(--buildings "$b/buildings_ch.gpkg")
else
  # the newest flight of each sheet (MapSetup: SwissStacUtil.NewestSheetFile), every sheet on disk: the stage reads
  # each sheet's bounds once and keeps only those touching the frames
  while IFS= read -r zip; do buildings+=(--buildings-gdb "$zip"); done < <(
    ls "$b"/swissbuildings3d_3_0_*.gdb.zip 2>/dev/null | sort |
      awk '{ k = $0; sub(/.*swissbuildings3d_3_0_[0-9]+_/, "", k); last[k] = $0 } END { for (k in last) print last[k] }' | sort)
fi

# stages still to run: those after FROM, else those without a marker
todo=()
hit=0
for s in "${STAGES[@]}"; do
  [ -n "${FROM:-}" ] && [ "$s" = "$FROM" ] && hit=1
  if [ -n "${FROM:-}" ]; then [ $hit = 1 ] && todo+=("$s"); elif [ ! -f "$TEMP/stage_$s.done" ]; then todo+=("$s"); fi
done
wants() { local s; for s in "${todo[@]}"; do [ "$s" = "$1" ] && return 0; done; return 1; }
wants_any() { local s; for s in "$@"; do wants "$s" && return 0; done; return 1; }

# fetch hints: what tools/swiss_data.py (stdlib Python, runs on the NAS too) gets, into the NAS's data folder
fetch() { echo "      fetch: python3 tools/swiss_data.py $1 --out '$DATA/$2'${3:+   then $3}"; }
bad=0
need() { # need <ok?> <what> <hint...>
  if [ "$1" = 1 ]; then echo "  ok       $2"; else echo "  MISSING  $2"; shift 2; "$@"; bad=1; fi
}
optional() { # optional <ok?> <what> <without it>
  if [ "$1" = 1 ]; then echo "  ok       $2"; else echo "  absent   $2: $3"; fi
}
is() { [ -n "$1" ] && [ -e "$1" ] && echo 1 || echo 0; }

say "sources for: ${todo[*]:-nothing (every stage done)}"
if wants heights; then
  n=$(ls "$IN_DIR" 2>/dev/null | grep -c '\.xyz\.zip$' || true)
  m=$(wc -l < "$IN_DIR.missing" 2>/dev/null || echo 0)
  if [ "$n" -gt 0 ] && { [ "$m" = 0 ] || [ -n "${MISSING_TILES_OK:-}" ]; }; then
    echo "  ok       swissALTI3D 0.5 m: $n tiles of the selection ($DATA/swiss_chunks)${MISSING_TILES_OK:+, $m without a zip (MISSING_TILES_OK=1)}"
  else
    echo "  MISSING  swissALTI3D 0.5 m: $m of $((n + m)) tiles of the selection have no zip in $DATA/swiss_chunks (listed in $IN_DIR.missing:"
    echo "           $(head -5 "$IN_DIR.missing" 2>/dev/null | tr '\n' ' ')...); MISSING_TILES_OK=1 builds without them (lake, abroad)"
    fetch "swissalti3d --tiles-file '$IN_DIR.missing'" swiss_chunks; bad=1
  fi
  [ -n "${ALTI2M:-}" ] && need "$(is "$alti2m")" "swissALTI3D 2 m ($alti2m, ALTI2M=1)" fetch "swissalti3d --res 2 --bbox ..." swissalti3d_2m
  [ -n "${RGEALTI:-}" ] && need "$(is "$rgealti")" "IGN RGE ALTI ($rgealti, RGEALTI=1)" echo "      fetch: tools/build-france.sh (docs/notes/tools/france-nas-run.md)"
fi
wants_any roads sources places cover trees files &&
  need "$(is "$tlm")" "swissTLM3D GeoPackage (${tlm:-$DATA/tlm3d/*.gpkg})" fetch swisstlm3d tlm3d "unzip the .gpkg.zip there (python3 -m zipfile -e <zip> '$DATA/tlm3d')"
wants_any buildings places &&
  need "$(is "$gwr")" "GWR cadastre ($gwr)" fetch "gwr --canton ch" gwr "unzip data.sqlite from gwr_ch.zip there (python3 -m zipfile -e '$DATA/gwr/gwr_ch.zip' '$DATA/gwr')"
if wants buildings; then
  nb=$(( ${#buildings[@]} / 2 ))
  need "$([ $nb -gt 0 ] && echo 1 || echo 0)" "swissBUILDINGS3D: $( [ -f "$b/buildings_ch.gpkg" ] && echo "buildings_ch.gpkg" || echo "$nb sheet zips (newest flight each)") in $b" \
    fetch "swissbuildings3d --tiles-file <the selection>" buildings3d
fi
if [ -z "${NO_OSM:-}" ] && wants_any sources fields pistes files; then
  need "$(is "$pbf")" "OpenStreetMap extract (${pbf:-$DATA/osm/switzerland-*.osm.pbf}; NO_OSM=1 builds without: no OSM lanes or signals, no pistes)" fetch osm osm
fi
wants water && optional "$(is "$bathy")" "swissBATHY3D ($bathy)" "every lake bed synthetic (fetch: python3 tools/swiss_data.py swissbathy3d --out '$bathy')"
wants fields && optional "$(is "$lwb")" "LWB farm fields ($lwb)" "OpenStreetMap fields only (fetch: python3 tools/swiss_data.py lwb --out '$lwb')"
wants files && optional "$(is "$boundaries")" "swissBOUNDARIES3D (\$DATA/boundaries/swissboundaries3d_*.gpkg)" "the POIs carry no canton"
if [ $bad = 1 ]; then say "SOURCES MISSING: nothing was built; fetch them (above) and start again"; exit 3; fi
say "sources OK"
[ -n "${CHECK_ONLY:-}" ] && exit 0

# ---- the run --------------------------------------------------------------------------------------------------
mkdir -p "$OUT" "$TEMP"
[ -n "${FROM:-}" ] && for s in "${todo[@]}"; do rm -f "$TEMP/stage_$s.done"; done

child=0
trap '[ $child != 0 ] && kill -TERM $child 2>/dev/null; wait; say "stopped (SIGTERM)"; exit 143' TERM INT

# stage <name> <args...>: one preprocessor mode, timed, with its peak RSS
stage() {
  local name=$1; shift
  if [ -f "$TEMP/stage_$name.done" ]; then say "stage $name: done earlier, skipped"; return 0; fi
  say "stage $name: start"
  echo "  TerrainPreprocessor $*"
  local t0=$SECONDS hwm=0 v rc
  nice -n "$NICE" ionice -c2 -n7 "$BIN/TerrainPreprocessor" "$@" < /dev/null &
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

R=(--cube-out "$OUT")
J=(--jobs "$JOBS")
# the stages, MapSetup's order and arguments (Pipeline.AddCubeSteps), with --jobs
h=("${R[@]}" --in "$IN_DIR")
[ -n "${ALTI2M:-}" ] && h+=(--in "$alti2m")
[ -n "${RGEALTI:-}" ] && h+=(--rgealti "$rgealti")
stage heights --cube "${h[@]}" --cube-box "$BOX" --cube-cache "$TEMP/cube_cache" "${J[@]}"
stage roads --cube-roads "${R[@]}" --tlm "$tlm" --cube-box "$BOX" "${J[@]}"
g=(); [ -f "$gwr" ] && g=(--gwr "$gwr")
stage buildings --cube-buildings "${R[@]}" "${buildings[@]}" "${g[@]}" --cube-box "$BOX" "${J[@]}"
o=(); [ -n "$pbf" ] && o=(--osm-pbf "$pbf")
stage sources --cube-sources "${R[@]}" --tlm "$tlm" "${o[@]}" "${J[@]}"
stage places --cube-places "${R[@]}" --gwr "$gwr" --tlm "$tlm" "${J[@]}"
stage network --cube-network "${R[@]}" --cube-box "$BOX" --frames-parallel "$FRAMES" "${J[@]}"
stage cover --cube-cover "${R[@]}" --tlm "$tlm" --cube-box "$BOX" "${J[@]}"
w=(); [ -d "$bathy" ] && w=(--bathy "$bathy")
stage water --cube-water "${R[@]}" "${w[@]}" "${J[@]}"
stage covernet --cube-cover-network "${R[@]}" --cube-box "$BOX" "${J[@]}"
stage trees --cube-trees "${R[@]}" --tlm "$tlm" --cube-box "$BOX" "${J[@]}"
f=(); [ -d "$lwb" ] && f=(--fields "$lwb")
[ -n "$pbf" ] && [ -f "$gwr" ] && f+=(--osm-pbf "$pbf" --gwr "$gwr")
if [ ${#f[@]} -gt 0 ]; then stage fields --cube-fields "${R[@]}" "${f[@]}" --cube-box "$BOX" "${J[@]}"
else say "stage fields: no LWB and no OpenStreetMap, skipped"; fi
if [ -n "$pbf" ]; then stage pistes --cube-pistes "${R[@]}" --osm-pbf "$pbf" --cube-box "$BOX" "${J[@]}"
else say "stage pistes: no OpenStreetMap (NO_OSM=1), skipped"; fi
a=(--tlm "$tlm" "${o[@]}" "${g[@]}"); [ -n "$boundaries" ] && a+=(--boundaries "$boundaries")
stage files --cube-region-files "${R[@]}" "${a[@]}" "${J[@]}"

say "leaves: $(ls "$OUT" | grep -c '\.cubl$') bundles, $(du -sh "$OUT" | cut -f1); $(grep -o '"key"' "$OUT/cube_manifest.json" 2>/dev/null | wc -l) manifest entries"
say "ALL STAGES DONE"
