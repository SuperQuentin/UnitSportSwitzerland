# Whole Switzerland in tile format v3 on the NAS (#1421)

`tools/build-swiss-nas.sh` rebuilds the whole Swiss map from the raw data under `data/` **on the TrueNAS box**
(`/mnt/Data/Documents/UnitSportSwitzerland` = `X:\UnitSportSwitzerland`) in tile format v3
(`terrain/height-format-v3`, `--height-format wide`) into `terrain_chunks_v3` (+ `terrain_chunks_v3_temp`,
log `terrain_chunks_v3.log`). The v2 build `terrain_chunks` is never written. Same runner as `france-nas-run`
(self-contained linux-x64 publish copied by `tar | ssh`, `setsid nohup nice ionice`, pidfile, `bash -s` because
the login shell is zsh, no root); the shared bits are `tools/lib/nas.sh`, the stages `tools/lib/swiss-nas-chain.sh`.

```
tools/build-swiss-nas.sh start             # publish, copy, run every unfinished stage detached (again = resume)
FROM=features tools/build-swiss-nas.sh start   # redo that stage and the later ones (a rule changed in the feature pass)
tools/build-swiss-nas.sh status | stop | log [N]
tools/build-swiss-nas.sh test geneva | basel | E0,N0,E1,N1   # every stage on a small box, in terrain_chunks_v3_test/<name>
```

Stages, in one NAS-side process (a stage that ends leaves `terrain_chunks_v3_temp/stage_<name>.done`; the log has
`=== <time> stage <name>: done in N s, peak RSS M MB`):

| stage | what | as |
|---|---|---|
| `terrain` | swissALTI3D `.terr`/`.terrc`, manifest, horizon | `rebuild-map.sh` terrain, plus `--height-format wide` |
| `frdem` | RGE ALTI over `FR_BOX` (2494,1099,2566,1146: Chablais and Geneva, as #939), dz -0.28, Swiss border tiles `--dem-reparse`d | `foreign-terrain` |
| `dem` | Baden-Wuerttemberg +0.23 (UTM32), BEV +0.12 (LAEA), Bolzano +0.08 (UTM32) over what was downloaded (`OTHER_BOX=auto`) | #968 |
| `osm` | `--osm-overlay` of the newest `switzerland-*.osm.pbf` | `rebuild-map.sh` |
| `features` | `--features-only --tlm --route-keys --cover --bathy --buildings-gdb (newest flight per sheet) --gwr --places` | `rebuild-map.sh all`; **rerun alone after a change to the feature pass** (`FROM=features`) |
| `airports` | `--airports` | `rebuild-map.sh` |
| `hash` | `--manifest-hash` once at the end (#924); `NO_HASH=1` skips | the other stages run `--no-hash` |

- `FR_BOX` has no `--dem-only`: with all of France on disk the Pays de Gex and the Jura inside the box are used too
  (`FR_DEPTS=D074` limits it to the old extent). Outside the box France is not merged (that is `france-nas-run`).
- French features (`--france`, BD TOPO WFS) are not part of the current build, so not of this one.
- Memory: `HEAP_GB` 16 (GC hard limit), `DEM_CACHE_MB` 4096, jobs = `nproc`. Disk: ~1 TB for the v2 build; the pool has ~11 TB free.
- Resume: a killed or stopped run restarts at its unfinished stage; the terrain stage skips tiles with a valid
  `.terr`+`.edge`. The feature pass redoes its tiles from the start of the pass.
- Mixing is refused: the preprocessor stops if `--out` already holds the other version, so a v3 run never writes into `terrain_chunks`.

## Test numbers (`test geneva`, 36 tiles, 12 threads; `test basel`, 36 tiles)

Geneva / Basel, seconds: terrain 3 / 3, frdem 134 / 79 (585k rasters scanned in 2 s, then ~130 s reading the edge
masks of the 8k edge tiles of France: a fixed cost of each start of that stage), dem 4 / 4, osm 7 / 9, features
568 / 485 (the 3.4M GWR records and 3,230 building sheets load first: a fixed cost, ~4 GB RSS peak), airports 4,
hash 1; wall 722 s. Peak RSS: terrain 0.6 GB, frdem 1.3 GB, osm 1.7 GB, features 4.0 GB. All `.terr` carry version
word 3, the manifest says `formatVersion` 3, `heightOffsetM` -50. Fixed costs dominate a small box, so these do not
extrapolate to the 43,621 tiles; the stage log of the first full run is the real number.
