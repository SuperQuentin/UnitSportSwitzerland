#!/usr/bin/env bash
# Terrain builder benchmark (#1539): a fixed region built by a published binary, timed per stage, every
# output file hashed, so an optimisation proves it is faster AND byte-identical.
#
#   tools/builder-bench.sh prepare                      copy the benchmark's source tiles and lake bed
#   tools/builder-bench.sh publish <label> [worktree]   dotnet publish -c Release -> <bench>/bin/<label>
#   tools/builder-bench.sh run <label> <bin-dir|label> [--jobs N] [--tiles FILE [--src DIR]]
#                                                       the whole `rebuild-map.sh all` pipeline, fresh output
#                                                       (--src: a kept copy of FILE's sources, so runs compare)
#   tools/builder-bench.sh compare <labelA> <labelB>    differing files (exit 1) + per-stage table
#
# Region (docs/notes/tools/builder-bench.md): Geneva and the lake, km tiles E 2498-2503 x N 1116-1121 (36),
# Valais mountains and Rhone valley, E 2582-2585 x N 1110-1113 (16).
# Environment: BENCH (default D:/unitsport_bench, /tmp/unitsport_bench elsewhere), UNITSPORT_DATA (source
# data, default ressources/data), BENCH_LAKES (lake beds to copy, default lacleman).
# Run it from a checkout whose docs/data/cover_overrides.json is the one to build with (the builder reads it
# relative to the working directory).
set -euo pipefail
cd "$(dirname "$0")/.."
repo=$PWD
here=$repo/tools

# Files that legitimately differ between identical runs (a timestamp, a path or a machine value inside).
# They are left out of sha256.txt and named in sha256.excluded.txt, never dropped silently.
# One extended regex per entry, matched against the path relative to the run dir; say why in a comment.
EXCLUDES=(
  # its "timing:" line holds wall seconds and the peak working set of the run
  '^chunks_temp/osm_overlay_report\.txt$'
  # holds each file's mtime (the files' own content hashes also sit in chunks/manifest.json)
  '^chunks_temp/tile_hashes\.tsv$'
)

pyc() { if python3 -c "" >/dev/null 2>&1; then echo python3; else echo python; fi; }
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }
if [ -n "${BENCH:-}" ]; then bench=$BENCH
elif [ -d /d ]; then bench=/d/unitsport_bench
else bench=/tmp/unitsport_bench; fi
data=${UNITSPORT_DATA:-ressources/data}
lakes=${BENCH_LAKES:-lacleman}

region_tiles() {
  local e n
  for ((e = 2498; e <= 2503; e++)); do for ((n = 1116; n <= 1121; n++)); do echo "$e-$n"; done; done
  for ((e = 2582; e <= 2585; e++)); do for ((n = 1110; n <= 1113; n++)); do echo "$e-$n"; done; done
}

# copy the newest-year zip of each tile listed in $1 into $2, report the tiles with no source
copy_sources() {
  local list=$1 dest=$2 missing=0 total=0 t f
  mkdir -p "$dest"
  while IFS= read -r t; do
    [ -n "$t" ] || continue
    total=$((total + 1))
    f=$(ls "$data/swiss_chunks/"swissalti3d_*_"$t"_0.5_2056_5728.xyz.zip 2>/dev/null | sort | tail -1 || true)
    if [ -z "$f" ]; then echo "MISSING source tile $t"; missing=$((missing + 1)); continue; fi
    [ -f "$dest/$(basename "$f")" ] || cp "$f" "$dest/"
  done < "$list"
  echo "sources: $((total - missing)) of $total tiles in $dest ($missing missing)"
}

cmd=${1:-}
case $cmd in
prepare)
  mkdir -p "$bench"
  region_tiles > "$bench/tiles.txt"
  copy_sources "$bench/tiles.txt" "$bench/src"
  mkdir -p "$bench/bathy3d"
  for l in $lakes; do
    f=$(ls "${UNITSPORT_BATHY:-$data/bathy3d}/"swissbathy3d_"$l"_*.zip 2>/dev/null | head -1 || true)
    if [ -z "$f" ]; then echo "MISSING lake bed $l"; continue; fi
    [ -f "$bench/bathy3d/$(basename "$f")" ] || cp "$f" "$bench/bathy3d/"
  done
  du -sh "$bench/src" "$bench/bathy3d"
  ;;
publish)
  label=${2:?label}; wt=${3:-.}
  dotnet publish "$wt/tools/TerrainPreprocessor/TerrainPreprocessor.csproj" -c Release -o "$(native "$bench/bin/$label")"
  ;;
run)
  label=${2:?label}; bin=${3:?binary dir or published label}; shift 3
  [ -d "$bin" ] || bin=$bench/bin/$bin
  jobs=(); tiles=$bench/tiles.txt; shared=
  while [ $# -gt 0 ]; do
    case $1 in
      --jobs) jobs=(--jobs "$2"); shift 2 ;;
      --tiles) tiles=$2; shift 2 ;;
      --src) shared=$2; shift 2 ;;
      *) echo "unknown option $1" >&2; exit 2 ;;
    esac
  done
  [ -f "$bin/TerrainPreprocessor.dll" ] || { echo "no TerrainPreprocessor.dll in $bin" >&2; exit 2; }
  run=$bench/runs/$label
  [ ! -e "$run" ] || { echo "$run exists; pick a new label or delete it" >&2; exit 2; }
  mkdir -p "$run"
  src=$bench/src
  if [ -n "$shared" ]; then src=$shared; copy_sources "$tiles" "$src"
  elif [ "$tiles" != "$bench/tiles.txt" ]; then src=$run/src; copy_sources "$tiles" "$src"; fi
  chunks=$run/chunks
  tlm=$(ls -t "$data"/tlm3d/*.gpkg | head -1)
  pbf=$(ls -t "$data"/osm/switzerland-*.osm.pbf | head -1)
  pre=(dotnet "$(native "$bin/TerrainPreprocessor.dll")" ${jobs[@]+"${jobs[@]}"})
  N() { native "$1"; }
  feat=(--out "$(N "$chunks")" --features-only --tlm "$(N "$tlm")" --route-keys "$(N "$data/routes/route_keys.sqlite")"
        --cover --bathy "$(N "$bench/bathy3d")" --buildings "$(N "$data/buildings3d/buildings_ch.gpkg")"
        --gwr "$(N "$data/gwr/data.sqlite")" --places --tiles-file "$(N "$tiles")")
  log=$run/log.txt
  t0=$(date +%s.%N)
  {
    echo "==== terrain"; "${pre[@]}" --in "$(N "$src")" --out "$(N "$chunks")"
    echo "==== osm overlay"; "${pre[@]}" --out "$(N "$chunks")" --tlm "$(N "$tlm")" --osm-overlay "$(N "$pbf")"
    echo "==== features"; "${pre[@]}" "${feat[@]}"
    echo "==== airports"; "${pre[@]}" --out "$(N "$chunks")" --tlm "$(N "$tlm")" --osm "$(N "$pbf")" --airports
  } > "$log" 2>&1 || { echo "run failed, see $log" >&2; tail -20 "$log" >&2; exit 1; }
  t1=$(date +%s.%N)
  $(pyc) "$here/builder-bench-compare.py" stage "$log" > "$run/stage.tsv"
  # every file of chunks and chunks_temp, path relative to the run dir, sorted
  ( cd "$run"
    LC_ALL=C find chunks chunks_temp -type f | LC_ALL=C sort > files.tmp
    : > sha256.excluded.txt
    for rx in ${EXCLUDES[@]+"${EXCLUDES[@]}"}; do grep -E "$rx" files.tmp >> sha256.excluded.txt || true; done
    LC_ALL=C sort -u sha256.excluded.txt -o sha256.excluded.txt
    LC_ALL=C grep -vxFf sha256.excluded.txt files.tmp | tr '\n' '\0' | xargs -0 sha256sum > sha256.txt || true
    rm files.tmp )
  peak=$(sed -n 's/.*peak_ws_mb=\([0-9]*\).*/\1/p' "$log" | sort -n | tail -1)
  bytes=$(du -sb "$run/chunks" "$run/chunks_temp" | awk '{ s += $1 } END { print s }')
  printf 'label=%s jobs=%s wall_s=%.1f peak_ws_mb=%s bytes=%s files=%s\n' "$label" "${jobs[1]:-all}" \
    "$(awk -v a="$t0" -v b="$t1" 'BEGIN { print b - a }')" "${peak:-0}" "$bytes" "$(wc -l < "$run/sha256.txt")" > "$run/summary.txt"
  cat "$run/summary.txt"
  ;;
compare)
  a=${2:?labelA}; b=${3:?labelB}
  exec "$(pyc)" "$here/builder-bench-compare.py" compare "$bench/runs/$a" "$bench/runs/$b"
  ;;
*)
  sed -n '2,13p' "$0" >&2; exit 2 ;;
esac
