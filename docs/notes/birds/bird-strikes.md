# Bird strikes

- **Bird strikes** (`BirdLife.CheckStrikes`/`SpawnAhead`/`TracerHit`, `FootPlayer.BirdStrike`, #8): the
  local pilot's craft is a set of spheres (plane 4.5 m, helicopter rotor disc 5 m, canopy pilot 0.7 m +
  wing 4 m, wingsuit 1 m); a live bird inside one dies (the normal fall + feathers) and hits the craft
  with **½·m·v²** at the closing speed. Mass is **9·L³** from the species length (no mass column: a
  sparrow 30 g, a goose 5 kg, capped at 12 kg because a swan would come out at 30). Damage is J/100 for a
  vehicle (cap 70, through `ShotHit`, so 0 HP wrecks it the usual way) and J/20 for a person (cap 60).
  **Over 1,500 J through the plane's nose half or anywhere into the helicopter stops the engine**
  (`EngineOn = false` → the existing glide / autorotation), announced "BIRD STRIKE — ENGINE OUT"; a
  banner only from 1 point of damage up. Measured: sparrow at 168 km/h 26 J → 0.3; greylag goose at
  ~130 km/h 2–2.7 kJ → 20–27 + engine out. **Birds are where aircraft fly**: every 2.5 s a flying,
  fast player gets one bird (or flock) 120–220 m ahead, within 60 m of the track, at the pilot's
  height above ground clamped to the species' real ceiling (soarers 400 m, gulls/kites 200, swifts 150,
  raptors/herons/waterfowl 150, the rest 50) — same 32-bird budget. Each airborne bird the craft will
  reach within 1.2 s gets ONE dodge roll (88% for a sparrow down to 55% for an eagle). Measured with
  `--survey 10` over Mollendruz, 60 m AGL at 160 km/h: **6 strikes per 10 min**, mostly tits and
  thrushes, one buzzard, one raven. **Aircraft guns hit birds**: tracers are also tested against the
  local bird list (segment vs `HitRadius` sphere); a round from THIS client bags the bird in the
  journal, so a protected species shot from a plane costs the −250 it costs on foot. Birds are local
  per client, so strikes are client-authoritative like the guns. Note the plane's wing guns fire
  **parallel** to the nose 1.7 m either side — they do not converge — and birds past
  `DespawnDistance` (240 m) do not exist, so a bird can only be shot inside that. Check:
  `<godot> --path . -- --birdstrikecheck[,out.png] [--at E,N] [--survey <min>]` — sparrow then
  goose on the nose line and a protected buzzard on a gun line; non-zero exit unless the struck birds
  die, the goose costs >10× the sparrow and stops the engine, the buzzard falls to the rounds (not a
  strike) with the penalty, and the plane still flies. Works with no terrain; `--survey` needs it.
