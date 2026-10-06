#!/usr/bin/env bash
# Rebuild a map that is already built, for the whole map or one area, from the source data on
# disk (nothing is downloaded). Run by the VS Code tasks "data: rebuild all" and "data: rebuild
# roads and signals".
#
# Usage: tools/rebuild-map.sh <what> [area] [--force] [--dry-run]
#   what  all          every stage: terrain (whole map only; unchanged source tiles are skipped
#                      unless --force), the OSM overlay and airports (when a switzerland-*.osm.pbf
#                      was downloaded), roads, buildings, land cover, trees, water, landings, places
#         roads        roads, junctions, markings and traffic lights again from swissTLM3D, then
#                      land cover and trees (masked off the new road lines), water, landings
#         roads-only   no cover pass: faster, trees and town paving keep the old road lines
#         osm          the OSM overlay first (one-way, lanes, signal nodes), then as roads
#   area  (none)            ask: the map, the whole map, or a typed box or point
#         pick              pick it on the map of Switzerland, the selector of the region setup
#                           wizard (MapSetup --pick-tiles): rectangle, brush, town + radius, canton
#         all               every built tile
#         E0,N0,E1,N1       a box
#         E,N,radiusKm      a square around a point
#         in LV95 metres (2582000,1108000,...) or km tile numbers (2582,1108,...)
#   --force                 with all: parse every swissALTI3D source tile again
#   --dry-run               print the commands, run nothing
# Tiles and source data are found through terrain_location.json (docs/notes/terrain/data-location.md),
# else terrain_chunks/ and ressources/data/; UNITSPORT_CHUNKS and UNITSPORT_DATA override both.
# An area rebuilds its own tiles only: a seam to a tile outside it keeps the old far side.
# Not rebuilt: SWISSIMAGE photos (--photos), French features (--france), the cycle route keys.
set -euo pipefail
cd "$(dirname "$0")/.."

dry=0; force=0; pos=()
for a in "$@"; do
  case $a in
    --dry-run) dry=1 ;;
    --force) force=1 ;;
    *) pos+=("$a") ;;
  esac
done
what=${pos[0]:-}; area=${pos[1]:-}
usage="Usage: tools/rebuild-map.sh <all|roads|roads-only|osm> [pick | all | E0,N0,E1,N1 | E,N,radiusKm] [--force] [--dry-run]"
case $what in
  all|roads|roads-only|osm) ;;
  *) echo "$usage" >&2; exit 2 ;;
esac

# terrain_location.json: {"data": "...", "chunks": "..."}, either key may be missing
located() {
  local v=""
  if [ -f terrain_location.json ]; then
    v=$(sed -n 's/.*"'"$1"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' terrain_location.json)
  fi
  v=${v//\\\\//}   # JSON-escaped Windows separators
  # a file written on Windows, read from WSL: D:/x is /mnt/d/x there
  if [[ $v =~ ^([A-Za-z]):/(.*)$ ]]; then
    local drive; drive=$(echo "${BASH_REMATCH[1]}" | tr '[:upper:]' '[:lower:]')
    if [ -d "/mnt/$drive" ]; then v=/mnt/$drive/${BASH_REMATCH[2]}; fi
  fi
  echo "${v:-$2}"
}
chunks=${UNITSPORT_CHUNKS:-$(located chunks terrain_chunks)}
data=${UNITSPORT_DATA:-$(located data ressources/data)}
chunks=${chunks%/}; temp=${chunks}_temp

[ -f "$chunks/manifest.json" ] || { echo "No built terrain at $chunks (manifest.json missing)" >&2; exit 1; }
tlm=$(ls -t "$data"/tlm3d/*.gpkg 2>/dev/null | head -1 || true)
[ -n "$tlm" ] || { echo "No swissTLM3D .gpkg in $data/tlm3d" >&2; exit 1; }
pbf=$(ls -t "$data"/osm/switzerland-*.osm.pbf 2>/dev/null | head -1 || true)
echo "Tiles: $chunks"
echo "TLM:   $tlm"

# each step says what it is before its command: the tools start quietly (dotnet builds first)
step() {
  echo
  echo "==== $1 ===="
}
run() {
  echo "+ $*"
  [ $dry = 1 ] || "$@"
}
pre=(dotnet run --project tools/TerrainPreprocessor -c Release --)

# ---- no area given: ask, the map first ----
if [ -z "$area" ]; then
  [ -t 0 ] || { echo "$usage" >&2; exit 2; }
  echo "Area to rebuild:"
  echo "  Enter     pick it on the map"
  echo "  all       the whole map"
  echo "  or type a box E0,N0,E1,N1 or a point E,N,radiusKm (LV95 metres or km tile numbers)"
  read -rp "> " area || true
  area=${area:-pick}
fi

# ---- area -> tiles file (one E-N per line; the preprocessor keeps the ones that are built) ----
tiles=()
tiles_file=$temp/rebuild_map_tiles.txt
if [ "$area" = pick ]; then
  # picking writes nothing but the tiles file, so it runs under --dry-run too
  mkdir -p "$temp"; rm -f "$tiles_file"
  dotnet run --project tools/MapSetup -c Release -- --pick-tiles "$tiles_file" --chunks "$chunks" --data "$data"     || { echo "Nothing picked, nothing rebuilt."; exit 1; }
  echo "Area:  $(grep -c . "$tiles_file") tiles picked on the map"
  tiles=(--tiles-file "$tiles_file")
elif [ "$area" != all ]; then
  IFS=', ' read -ra v <<< "$area"
  for i in "${!v[@]}"; do
    v[$i]=${v[$i]%%.*}
    [[ ${v[$i]} =~ ^[0-9]+$ ]] || { echo "Bad area: $area (all, E0,N0,E1,N1 or E,N,radiusKm)" >&2; exit 2; }
  done
  metres=$(( ${v[0]:-0} >= 100000 ))
  case ${#v[@]} in
    4)
      if [ $metres = 1 ]; then
        e0=$(( v[0] / 1000 )); n0=$(( v[1] / 1000 )); e1=$(( (v[2] - 1) / 1000 )); n1=$(( (v[3] - 1) / 1000 ))
      else
        e0=${v[0]}; n0=${v[1]}; e1=${v[2]}; n1=${v[3]}
      fi ;;
    3)
      if [ $metres = 1 ]; then
        e0=$(( (v[0] - v[2] * 1000) / 1000 )); e1=$(( (v[0] + v[2] * 1000) / 1000 ))
        n0=$(( (v[1] - v[2] * 1000) / 1000 )); n1=$(( (v[1] + v[2] * 1000) / 1000 ))
      else
        e0=$(( v[0] - v[2] )); e1=$(( v[0] + v[2] )); n0=$(( v[1] - v[2] )); n1=$(( v[1] + v[2] ))
      fi ;;
    *) echo "Bad area: $area (all, E0,N0,E1,N1 or E,N,radiusKm)" >&2; exit 2 ;;
  esac
  (( e1 >= e0 )) || e1=$e0
  (( n1 >= n0 )) || n1=$n0
  count=$(( (e1 - e0 + 1) * (n1 - n0 + 1) ))
  (( count <= 100000 )) || { echo "Area too large: $count tiles (E $e0..$e1, N $n0..$n1)" >&2; exit 2; }
  echo "Area:  tiles E $e0..$e1, N $n0..$n1 ($count)"
  if [ $dry = 0 ]; then
    mkdir -p "$temp"
    for (( e = e0; e <= e1; e++ )); do
      for (( n = n0; n <= n1; n++ )); do echo "$e-$n"; done
    done > "$tiles_file"
  fi
  tiles=(--tiles-file "$tiles_file")
else
  echo "Area:  the whole map"
fi

if ls "$chunks"/*.road.swiss > /dev/null 2>&1; then
  echo "Note: this map holds French features (.road.swiss); run --france again on the rebuilt border tiles"
fi

# ---- terrain: the source tiles decide what is built, so it cannot be limited to an area ----
if [ $what = all ]; then
  if [ "$area" != all ]; then
    echo "Note: the terrain heights (.terr) of the area are kept as they are. That step only runs on"
    echo "      the whole map, and its tiles change only when the swissALTI3D source does. Everything"
    echo "      on top (roads, buildings, cover, water) is rebuilt for the area."
  elif [ ! -d "$data/swiss_chunks" ]; then
    echo "Note: no swissALTI3D tiles in $data/swiss_chunks, the terrain is kept as it is"
  else
    terrain=(--in "$data/swiss_chunks" --out "$chunks")
    if [ $force = 1 ]; then terrain+=(--force); fi
    step "Terrain: swissALTI3D -> .terr, far horizon (unchanged source tiles are skipped unless --force)"
    run "${pre[@]}" "${terrain[@]}"
  fi
fi

# ---- OSM overlay: always the whole map. The file is replaced, so one made for an area would
# leave every other tile without OSM data at its next rebuild. OSM is opt-in (ODbL,
# docs/notes/tools/osm-odbl-licence.md): all only uses it when the extract was downloaded ----
if [ $what = osm ] && [ -z "$pbf" ]; then
  echo "No switzerland-*.osm.pbf in $data/osm (MapSetup --layers roads,osm downloads it)" >&2; exit 1
fi
if [ $what = osm ] || { [ $what = all ] && [ -n "$pbf" ]; }; then
  step "OSM overlay, whole map: every TLM road line of the map's bounding box, then the OSM extract (minutes on a wide map)"
  run "${pre[@]}" --out "$chunks" --tlm "$tlm" --osm-overlay "$pbf"
elif [ ! -f "$temp/osm_nodes.tsv" ]; then
  echo "Note: no $temp/osm_nodes.tsv, so traffic lights come from the inference rule alone (osm builds it)"
fi

# ---- roads, buildings, cover (then water and landings), places: one feature pass ----
args=(--out "$chunks" --features-only --tlm "$tlm")
if [ -f "$data/routes/route_keys.sqlite" ]; then args+=(--route-keys "$data/routes/route_keys.sqlite"); fi
if [ $what != roads-only ]; then
  args+=(--cover)
  # surveyed lake beds where the zips are; without them the water pass makes every bed synthetic
  if [ -d "$data/bathy3d" ]; then args+=(--bathy "$data/bathy3d"); fi
fi
if [ $what = all ]; then
  # buildings: a nationwide GeoPackage, else the published sheet zips (the newest flight of each
  # sheet, as MapSetup picks them), else the GeoPackage an older MapSetup run exported
  b=$data/buildings3d
  buildings=()
  if [ -f "$b/buildings_ch.gpkg" ]; then
    buildings=(--buildings "$b/buildings_ch.gpkg")
  else
    while IFS= read -r zip; do buildings+=(--buildings-gdb "$zip"); done < <(
      ls "$b"/swissbuildings3d_3_0_*.gdb.zip 2>/dev/null | sort |
        awk '{ k = $0; sub(/.*swissbuildings3d_3_0_[0-9]+_/, "", k); last[k] = $0 } END { for (k in last) print last[k] }' | sort)
    if [ ${#buildings[@]} = 0 ] && [ -f "$b/mapsetup_selection.gpkg" ]; then
      buildings=(--buildings "$b/mapsetup_selection.gpkg")
    fi
  fi
  if [ ${#buildings[@]} = 0 ]; then
    echo "Note: no swissBUILDINGS3D data in $b, the buildings are kept as they are"
  else
    echo "Buildings: $(( ${#buildings[@]} / 2 )) source file(s) in $b"
    args+=("${buildings[@]}")
  fi
  if [ -f "$data/gwr/data.sqlite" ]; then
    args+=(--gwr "$data/gwr/data.sqlite" --places)
  else
    echo "Note: no $data/gwr/data.sqlite, so no place index and no building years or floors"
  fi
fi
case $what in
  all) step "Places, then roads and buildings, road network, cover and trees, water, landings (the long step)" ;;
  roads-only) step "Roads, then the road network (junctions, markings, traffic lights)" ;;
  *) step "Roads, road network (junctions, markings, traffic lights), cover and trees, water, landings" ;;
esac
run "${pre[@]}" "${args[@]}" ${tiles[@]+"${tiles[@]}"}

# ---- airports: stands and runway profiles from OSM, after the buildings they must stay clear of ----
if [ $what = all ] && [ -n "$pbf" ]; then
  step "Airports: stands and runway profiles from OSM"
  run "${pre[@]}" --out "$chunks" --tlm "$tlm" --osm "$pbf" --airports
fi
echo
echo "Rebuild done."
