# Trams (#1057)

Cosmetic, local per client like the cars (`traffic-trains`, base `traffic-modes`): blocky low-floor sections (#1147, `TrafficModeMeshBuilder.TramSection`) in 2.4 x 3.4 m boxes, three
sections 9/7/9 m or five 8.6/6.4/8.6/6.4/8.6 m, 0.6 m apart, `GameSettings.Trams` (0..20, Settings → World), `--trams N`.
In the test world trams come only from the generators at its tram track ends, one every 6 min each way (#1134, `traffic-generators`).

## Graph (`World/LaneGraph.Trams.cs`, `LaneGraph.BuildTrams`)

- Every `RoadFlags.Tramway` segment of the 7x7 km tiles (not disused, not a funicular), at its own height (no rail sink: a box
  stands on the road). TLM maps a tram line's tracks one by one (Geneva: all 227 tram segments single, none `DoubleTrack`) with
  no direction: `TramTracks.Directions` (`tools/TerrainFormat`, shared with RoadGen) samples each track every 4 m for a parallel
  partner 1.5-6 m to the side; partner on its left = trams run along its drawing (they keep right), on its right = against it,
  none = both ways. A `DoubleTrack` line runs both ways, each tram on its right track (`TrackOffset`, as a train).
- **Junction paths**: where RoadGen laid tram paths through a core (`JPTH` kind `Tram`, `tools/tram-approaches`), the tracks are
  cut at each path end (`TramCut`, within 0.75 m) and the piece between two ends of one core's paths is dropped; the path is a
  one-way edge with its `JunctionPathInfo` (lights, tram group). Its stop line: the `LANE` record with a tram lane facing the way
  the path starts, its distance before the path (`TramStopBack`).
- **Stops**: each `PointPropType.TramStop` sign (RoadGen, from `--tram-stops`) on the track within 4.5 m whose trams it faces
  with the sign on their right (`TramStopsOn`).
- Street running: `Embedded` tracks (`InStreet`): the tram is an in-lane crosser there and on its paths; cars behind follow it.

## Driving (`World/Traffic.Trams.cs`)

- Line speed 40 km/h in a street (embedded or a narrow town formation), 50 on its own bed, 25 through a core; curves at
  1.0 m/s² sideways read 140 m ahead; accelerates 1.0, service brake 1.2 (everything it plans for), up to 2.8 for what cuts in.
- Follows what is on its track ahead (within 1.9 m of its line): trams, cars (it queues with them in a shared lane), players.
- **Its light**: the path's tram group (it leads its approach's cars: `tram-approaches`; the probe counts trams let go from red ahead of their cars). Bar across (red) or red-amber: stop at the line; the dot (amber): stop if it can at 1.2,
  else clear; go bar: go. `TramRedRuns` must stay 0.
- **Stops**: brakes to stand with its front at the sign, doors 20 ± 6 s, then on (`ConditionalWeakTable` state, served once).
- Spawned 300-1500 m out, no nearer than 120 m to another, its sections laid back along the track (`BackFill`); at the end of
  the track it stands and is dropped after 20 s.
- Cars give way to trams: at lights where the plan makes the car's turn permissive against the tram group (a left turn across
  an oncoming tram, `ConflictPoints`); without lights at every point where a car path of the core crosses a tram path
  (`TramYields`, SVG Art. 38: trams have priority). Both wait through `GapWait`/`Coming`, trams being crossers of kind `Tram`.

## Check

`--trafficcheck --mover tram [--trams N]` prints a `trams:` line every 5 s (spawned, top speed, junction paths, stops, stops
served, stops at red, lines crossed, on red) and fails on a tram over its line on red. Windowed with a picture it saves
`<png>_tram_red.png` and `_tram_red_head.png` once a tram stands first at its red.

Measured (Oct 21 2026, headless `--fixed-fps 60`):
- Signal test region J14 (`--at 2917500,1323500 --trams 6 --seconds 150`): 6 track edges, 2 paths, 4 stops all served, 0 on red.
- Geneva copy rewritten with this branch (`D:\UnitSportSwitzerland\test1057\geneva`, `--at 2499901,1118599 --trams 20
  --traffic 150 --seconds 240`): 693 track edges, 231 junction paths, 75 stops (37 served), 23 stops at red, 82 lines crossed,
  0 on red; cars: no red run.

Not done: trams reverse at a stub terminus (they stand and are dropped), doors, OSM `railway=tram_stop`, cars keeping off tracks
outside junctions, a right-turning car across a tram on its own bed beside the road at lights (same arm: the chord model sees no
conflict).
