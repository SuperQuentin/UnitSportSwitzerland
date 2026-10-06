#!/usr/bin/env bash
# Re-run the road pass of TerrainPreprocessor on terrain that is already built: roads, junctions,
# markings and traffic lights again from swissTLM3D, draped on the existing .terr tiles, for the
# whole map or one area. Run by the VS Code task "data: rebuild roads and signals".
#
# Usage: tools/rebuild-roads.sh [area] [mode] [--dry-run]
#   area  all (default)     every built tile
#         E0,N0,E1,N1       a box
#         E,N,radiusKm      a square around a point
#         in LV95 metres (2582000,1108000,...) or km tile numbers (2582,1108,...)
#   mode  cover (default)   roads, then land cover and trees (masked off the new road lines), water, landings
#         roads             roads only: faster, trees and town paving keep the old road lines
#         osm               the OSM overlay first (one-way, lanes, signal nodes), then as cover
#   --dry-run               print the commands, run nothing
# Tiles and source data are found through terrain_location.json (docs/notes/terrain/data-location.md),
# else terrain_chunks/ and ressources/data/; UNITSPORT_CHUNKS and UNITSPORT_DATA override both.
# An area rebuilds its own tiles only: a seam to a tile outside it keeps the old far side.
set -euo pipefail
cd "$(dirname "$0")/.."

dry=0; pos=()
for a in "$@"; do
  case $a in
    --dry-run) dry=1 ;;
    *) pos+=("$a") ;;
  esac
done
area=${pos[0]:-all}; mode=${pos[1]:-cover}
case $mode in
  cover|roads|osm) ;;
  *) echo "Unknown mode: $mode (cover, roads or osm)" >&2; exit 2 ;;
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
echo "Tiles: $chunks"
echo "TLM:   $tlm"

run() {
  echo "+ $*"
  [ $dry = 1 ] || "$@"
}
pre=(dotnet run --project tools/TerrainPreprocessor -c Release --)

# ---- area -> tiles file (one E-N per line; the preprocessor keeps the ones that are built) ----
tiles=()
if [ "$area" != all ]; then
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
  tiles_file=$temp/rebuild_roads_tiles.txt
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

# ---- OSM overlay: always the whole map. The file is replaced, so one made for an area would
# leave every other tile without OSM data at its next rebuild ----
if [ $mode = osm ]; then
  pbf=$(ls -t "$data"/osm/switzerland-*.osm.pbf 2>/dev/null | head -1 || true)
  [ -n "$pbf" ] || { echo "No switzerland-*.osm.pbf in $data/osm (MapSetup --layers roads,osm downloads it)" >&2; exit 1; }
  run "${pre[@]}" --out "$chunks" --tlm "$tlm" --osm-overlay "$pbf"
elif [ ! -f "$temp/osm_nodes.tsv" ]; then
  echo "Note: no $temp/osm_nodes.tsv, so traffic lights come from the inference rule alone (mode osm builds it)"
fi

args=(--out "$chunks" --features-only --tlm "$tlm")
if [ -f "$data/routes/route_keys.sqlite" ]; then args+=(--route-keys "$data/routes/route_keys.sqlite"); fi
if [ $mode != roads ]; then args+=(--cover); fi
run "${pre[@]}" "${args[@]}" ${tiles[@]+"${tiles[@]}"}
