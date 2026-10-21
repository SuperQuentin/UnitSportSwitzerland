# Tram tracks as approach lanes (#1057)

The junction planner's cores (`junction-planner`) take the tram tracks running through them (`Rewrite/TileRewriter.Trams.cs`).

- **Tram ways**: `TramNet` holds the block's tram tracks (ribbons whose source is `Tramway`, not disused/funicular), each the way
  its trams run (`TramTracks.Directions`, the game's rule; both ways where unknown; a `DoubleTrack` line as its two right-hand
  tracks offset by `TrackOffset`). For each written core, every track crossing its outline inward is traced over the switches
  inside (straight on, cos > 0.5, as the trains) to where it leaves; the arms are the mouths each crossing lies within 3 m of.
  Same arm or no arm: left out (counted). Move: `SignalPlan.Turn` on the arms' headings.
- **`JPTH` kind `Tram`**: points on the track the trams run on, ends where the track's line crosses the mouths (not the road's
  axis), offsets as the track lies right of its line (+ at the start, - at the end, the usual convention), width 2.4 m, the
  approach's tram group at lights, -1 without.
- **Signal group** (`SignalGroupKind.Tram`, `SignalArm.TramMoves`, not stored): one per approach a tram enters by, added after
  every other group (plans without trams keep their groups). It joins its approach's phases where nothing in them conflicts hard
  with it (chords as cars), else the first phase that allows it, else a phase of its own; a car's left from a shared lane across
  it is permissive (the car gives way). Timed as a slow group (`Slow`: its long clearing ends its green early), its ways as
  conflict lines (`ConflictTimes.Mover.Tram`: clear and enter 7 m/s, 42 m long) from where its track crosses the cars' line.
- **Leading signal** (the user: "a short leading signal (1 sec). If there is conflicting movements, add a longer leading signal,
  like for bikes"): a tram group starts `SignalPlan.TramLead` 1 s before its approach's cars starting with it, the riders' lead
  (`BikeLead` 3 s) before a car group starting with it that turns across its way (permissive); conflict-timed: those cars wait
  that long after the tram (only where the tram may go no later than they); fixed-time: the phase change grows by it and the tram
  starts that much early once what it crosses has cleared (each its clearance and all-red). The rewrite counts the tram groups
  leading by 1-3 s, 3 s or more, or not at all (and where). J14: both lead by 3 s or more. Tier 0: `TramTests`.
- **Aspects** (the tram bar signal, `SignalLamps`): red and red-amber = the bar across (stop), amber = the dot (stop expected),
  green = the bar upright, slanted left or right for a tram group that only turns. White lamps in a black housing of three.
- **`LANE`**: a tram lane (`ApproachLaneKind.Tram`) on the lit approach's record where its track crosses the cars' line, left to
  right among the others; its moves the trams'.
- **Heads**: `SignalPoleFlags.Tram` (32) on the approach's main pole where the track lies right of the axis, else on its second
  (left or refuge) pole; drawn beside the car heads on the road's side (`SignalBuilder.TramHeads`). Model viewer:
  "Traffic light - Tram".
- **Stops** (`tram_stops.tsv`, preprocessor `--tram-stops --out <chunks> --tlm <gpkg>` into `<chunks>_temp`): swissTLM3D has no
  tram stop; a `tlm_oev_haltestelle` point (any kind) within 25 m of a TLM tram track is one. RoadGen (`Options.TramStops`)
  stands a `PointPropType.TramStop` sign 2.6 m right of every track within 15 m of a stop, the way its trams run, facing them;
  one per track and direction within 40 m. Geneva centre: 81 stops, 85 signs. Model viewer: "Road prop - TramStop".
- **Test region**: J14 has two tracks 3.2 m apart in the artery (`TTS`, `TTN`, TLM-like, `OnStreet`) and stops 150 m either side.
- **Numbers** (Geneva copy `D:\UnitSportSwitzerland\test1057\geneva`, from #1030's copy): 250 tram paths through 143 cores (90
  at lights, 90 tram groups, 78 tram lanes), 32 tracks through a core not from arm to arm, 85 stop signs.
