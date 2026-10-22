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
- **Signal group** (`SignalGroupKind.Tram`, `SignalArm.TramMoves`, not stored): one per approach a tram enters by and per
  direction its trams take there (left, straight, right: the user, a head per direction; transit gets directional signals on a
  single track, never combined: through can be go while left is stop, `OneTrackBranching_TimesEachDirectionOnItsOwn`, each
  tram path takes its own direction's group by `TramGroup`), added after every other group (plans
  without trams keep their groups). Each joins its approach's phases where nothing in them conflicts hard with it (chords as
  cars), else the first phase that allows it, else a phase of its own; a car's left from a shared lane across it is permissive
  (the car gives way). Timed as a slow group (`Slow`: its long clearing ends its green early), its ways as conflict lines
  (`ConflictTimes.Mover.Tram`: clear and enter 7 m/s, 42 m long) from where its track crosses the cars' line.
- **Leading signal** (the user: "a short leading signal (1 sec). If there is conflicting movements, add a longer leading signal,
  like for bikes"): a tram group starts `SignalPlan.TramLead` 1 s before its approach's cars starting with it, the riders' lead
  (`BikeLead` 3 s) before a car group starting with it that turns across its way (permissive); conflict-timed: those cars wait
  that long after the tram (only where the tram may go no later than they); fixed-time: the phase change grows by it and the tram
  starts that much early once what it crosses has cleared (each its clearance and all-red). The rewrite counts the tram groups
  leading by 1-3 s, 3 s or more, or not at all (and where). J14: both lead by 3 s or more. Tier 0: `TramTests`.
- **Aspects and heads per canton** (the user, Oct 21 2026):
  - **Romandy** (`SignalPlan.TramTHead`, bit 6 of the plan's flag byte; `RomandyTramHead`: GE, VD, NE, JU, FR, VS west of
    E 2 612 000 (the language border at Sierre as a straight cut; FR's German-speaking east is not told apart)): a one-lens car
    head module (#1105, the user's photos: `File:Tram_signals_Geneva.JPG`'s two square heads at the pole's foot, and two more;
    the round TPG heads are #1104): the car head's white plate, black housing, visor and 200 mm lens, the lens dark with a
    solid white T lit in it (`SignalLamps` `TramTStop`/`TramTGo`). Vertical stroke lit = go, horizontal stroke lit = stop,
    vertical flashing = the amber before red, horizontal flashing = the red-amber before green, which lasts `TramRedAmberT` 5 s
    (the user: "way longer than for cars"; no source found for the length; `SignalPlan.Build(..., tramTHead)` sets the flag
    before the timing: set after it, as until #1105, the 5 s never applied). The unlit stroke a shade off the lens (`TramTOff`),
    no bloom; a small "tram detected" dot above the T, modelled, never lit. Packed plate against plate with the car heads
    (`SignalBuilder.RomandyTramHeads`, `TramPack`, no arm): beside them on their right where there is one (level with their
    lower half, photo 1; over the main head's flasher where it has one: the user, "there is still space for one transit
    head") or more than there are car heads, up to `TramColumn` 2 without a flasher, stacked in a column down their side (the
    user: "stacked vertically"); else in a row on top of them ending over the main head, each its arrow plate between it and
    the car head below (photo 2). The arrow
    plate only where the approach's trams go several ways. A tram-only pole (repeater, pole-only placement): the same modules
    side by side, level with a car head's lower half. A tram's red-amber holds it at its line, so the fixed-time check does
    not count it as live.
  - **Other cantons**: three white lamps in a black housing, beside the car heads on the road's side: red and red-amber = the bar
    across (stop; red-amber unverified, the user does not know its look), amber = the dot (stop expected), green = the bar
    upright, slanted for a group that only turns; its arrow on a plate below where its trams go several ways.
- **`LANE`**: a tram lane (`ApproachLaneKind.Tram`) on the lit approach's record where its track crosses the cars' line, left to
  right among the others; its moves the trams'.
- **Poles** (the user: "pole only, repeated on both sides if repeater pole"): `SignalPoleFlags.Tram` (32) on the approach's main
  pole and on its repeater (the left kerb's or the refuge's `Second` pole) where it has one; never on a span wire or an arm. Model
  viewer: "Traffic light - Tram", "Tram, Romandy", "Tram, two ways", "Tram, Romandy, two ways".
- **Stops**: OSM's (`--osm-overlay` writes `osm_tram_stops.tsv` beside `osm_overlay.tsv`: `railway=tram_stop`, and
  `public_transport=stop_position` with `tram=yes`) merged with swissTLM3D's (`tram_stops.tsv`, preprocessor `--tram-stops --out
  <chunks> --tlm <gpkg>` into `<chunks>_temp`; TLM has no tram stop: a `tlm_oev_haltestelle` point of any kind within 25 m of a
  TLM tram track is one): a TLM stop within 25 m of an OSM one is left out (OSM's lies on the track). RoadGen (`Options.TramStops`,
  `ReadAllTramStops`) stands a `PointPropType.TramStop` sign 2.6 m right of every track within 15 m of a stop, the way its trams
  run, facing them; one per track and direction within 40 m. Geneva centre: 135 OSM + 81 TLM stops. Model viewer: "Road prop -
  TramStop".
- **Test region**: J14 has two tracks 3.2 m apart in the artery (`TTS`, `TTN`, TLM-like, `OnStreet`) and stops 150 m either side.
- **Switches**: tram tracks meeting at a node of their own run through it uncut (`PipelineOptions.ThroughNode`, the junction
  builder skips the node): a rail junction trimmed them back, a gap the trams could not cross (Geneva: 250 → 291 tram paths).
- **120 s kept** (`LayoutWrite`, after `Retime`): where trams take a plan past the legal maximum, their leads go first, then the
  plan is timed for a 26 m tram (`ConflictTimes.TramShortLength`), then for the cars' times alone (its trams out of the conflict
  times); the rewrite counts and names them. A tram group stays out of the phases where it would take the walkers' green it crosses
  (a turning tram over a crosswalk), keeping them at least one: J14's branch had grown a walkers' phase of its own (153 s).
- **Cantons**: `TileRewriter.Cantons.Find` looks for `switzerland.bin` in `tools/MapCore` (it moved there with #515; it looked in
  `MapSetup`, so real rewrites had no canton before #1057).
- **Numbers** (Geneva copy `D:\UnitSportSwitzerland\test1057\geneva`, from #1030's copy): 291 tram paths through 153 cores (110
  at lights), 108 tram groups (two approaches whose trams go two ways), 91 tram lanes, 54 tracks through a core not from arm to
  arm, 102 stop signs; leads 32 by 1-3 s, 56 by 3 s or more, 20 none; 3 junctions without leads to stay within 120 s, 2 of them
  with their trams out of the conflict times. Lights 243, 0 invalid, none over 120 s. Screenshot: a Romandy cluster at the pole
  LV95 2500000,1117246 (`--shot 1.5,g3.8,12,2,0,30,out.png --origin 2500000,1117250`).
