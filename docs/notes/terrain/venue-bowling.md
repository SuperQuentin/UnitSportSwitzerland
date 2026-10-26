# Bowling centres (#1232)

> Research for #1232, Oct 2026: tagged [S] sourced, [E]/[H] estimated, [D] derived. Constants built from [E] numbers stay tunable.

Ten-pin only. X = across the lane, Z = along it, foul line at Z = 0, pins at +Z. OSM has
`leisure=bowling_alley` 55 in Switzerland (`sport=10pin` 21, `sport=9pin` 16 for the old skittle alleys),
so bowling is placed by data only. Arcade corner and pool/darts props: `venue-arcade.md`, `venue-dining.md`.

## One lane

- **Foul line to head pin** 18.29 m (60 ft) [S]; to pit end 19.16 m [S]; head pin to tail board 0.868 m [S].
- **Pin spacing** 0.305 m centre to centre, equilateral triangle, rows of 1/2/3/4, row depth 0.264 m [D].
- **Playing surface** 1.06 m wide (rule 1.041-1.067) [S]; **gutter** 0.235 m wide, 0.048 m deep each side [S];
  lane + both gutters **1.525 m** [S].
- **Approach** 4.57 m min, real 4.6-5.2 m [S/E]. Foul line a thin dark strip across lane and approach.
  Approach dots (small black) 1.0 m before the line [E].
- **Arrows**: 7, 3.66-4.88 m past the foul line, 0.03 x 0.15 m [S].
- **Lane thickness** 0.05-0.064 m, surface ~0.75 m above the floor [S/E]. Lane tan maple or synthetic.
- **Pin** 0.381 m tall, 0.121 m max diameter, 0.052 m base, 1.5-1.6 kg, white with two red neck stripes [S].
- **Ball** 0.218 m diameter (0.216-0.218), up to 7.26 kg [S]; house balls 3-7 kg, kids 2.5-4 kg [E].
- **Pit**: ~0.9 m of lane, then a drop into a dark cushion 0.4-0.6 m deep [E]. **Pinsetter** ~2.44 m deep
  behind it, hood top 2.0-2.3 m [S/E]; grey/cream or blue-green.

## Lane block (front to back along Z)

1. **Approach**: pale polished wood, foul line at its end.
2. **Lane** 18.3 m tan, arrows and dots, even sheen.
3. **Gutters** dark grey/black concave. Between the two lanes of a pair a shared **capping board**
   ~0.25 m wide, 0.15 m above the lane, hiding both gutters and the ball-return track. Cross-section:
   lane | gutter | divider with return | gutter | lane [S].
4. **Pin deck** with 10 pins, one overhead spot per lane.
5. **Masking unit** across the pair, hides the machine: 3.5 m wide x 0.3 deep x 2.5-3.3 m tall [E],
   printed theme (racing flags, planets, cartoon, flames, sponsor ads) or plain colour with LED strip;
   lane numbers lit in the middle; a 1.06 x 0.8 m opening just above the deck shows the pins; red/green
   "bowl / don't bowl" light [S]. Top touches the ceiling or a canopy.
6. **Machine room** behind it: pinsetters visible only from the service aisle, 1.5 m rear mechanic
   walkway (min) [S], back wall.
7. **Ball return hood** (one per pair, in the divider at the approach end): 0.9 x 0.7 x 0.7 m,
   translucent coloured fibreglass (teal, amber, red, blue) or black with chrome trim, floor + 0.75 m,
   rack for the balls of 2 lanes, small fan vent [E]. A "thunk" when a ball emerges.
8. **Scoring**: per pair a monitor on a stand, or screens 0.9 x 0.1 x 0.55 m hung 2.5-3 m up (deep blue,
   white/yellow digits, strike animations); console 0.5 x 0.3 x 0.2 m on the scoring table [E].

## Block dimensions

- **Lane-pair pitch 3.50 m** (11 ft 6 in) [S]; 4 lanes 6.89 m; average 1.72-1.75 m per lane, planning
  rule 1.71 m (5.6 ft) per lane [S]. Lane count is **always even** (pairs).
- **Block length** approach to back of machine 25.4 m (83 ft 4.5 in) [S]; with the rear service
  aisle 27.0 m (88 ft 6 in, dealer quote) [S]. Build-up: approach 4.6 + lane 19.2 + machine 1.6-2.4 +
  aisle 1.5 = ~27 m [D].
- **Machine-side aisle between pairs** 0.9-1.4 m, service depth 0.43 m [S].
- **Ceiling** >= 3.05 m (10 ft) at the lane/equipment area, clear span preferred [S]; 3.0-3.5 m at the
  machine end, 4.0-6.0 m on mixed-use big boxes [E]. Columns on pair boundaries (one per 2nd pair) and
  in the settee row, never in a lane.
- **Footprint rules** [S]: back wall through the settee area 30.5 m; concourse + desk + restaurant
  15-23 m (50-75 ft); building depth 46-54 m for 24 lanes (single level ~41 m wide). Brunswick:
  **concourse ~9 m**, **seating min 3.7 m, preferred ~6.7 m**.
- **Area per pair** [D]: lane block 3.5 x 27.0 = 94.5 m2; + settee/scoring 13-23 m2 = 110-118 m2;
  + share of concourse/services = **160-190 m2 per pair** (80-95 m2 per lane). Rule of thumb
  **~93 m2 of building per lane** [S]. US land: 4,000-5,000 ft2 per lane incl. 5 parking spaces [S].
- **Width** = pairs x 3.5 m + 2 x 1.2 m side aisles. **Depth** = 27 + 3.7-6.7 settee + 10-20 concourse
  and services = **41-54 m** [D]. Groups of 8-12 lanes get a cross-aisle of 1.2-1.5 m [E].

## Zone order (Z from entrance towards the pins)

```
[entrance lobby] -> [concourse: desk, shoes, bar, arcade, pool]  15-23 m deep, full width
        | low rail or step 0.15-0.45 m, sight line over the settees
[settee row: benches facing the lanes, tables, scoring]  3.7-6.7 m
[approaches + lanes + masking line + machine room]  ~27 m
[back wall: mechanic room, storage, offices at one end of the machine corridor]
```

Lanes run along the long axis; the concourse sits in front of the foul-line end.
Concourse order [E]: lobby, desk + shoe counter (3-5 m) near the entrance, bar/snack counter
(5-10 m, ~4 seats per lane), arcade corner, pool hall (1 table per 3 lanes), darts, party room
(>= 12 lanes), WC (>= 2), back of house.

## Layout by size [E from 93 m2 per lane]

| Size | Lanes | Area | Zones |
|---|---|---|---|
| Small | 4-8 | 400-800 m2 (~20 x 30 m) | desk doubles as shoe counter and bar, 1-2 pool tables or darts, WC, store; snack bar at most; 2-4 arcade machines at the entrance |
| Medium | 12-20 | 1,100-1,900 m2 | desk + shoe wall, bar/snack 15-30 seats, 3-8 pool tables, darts, arcade corner 8-20 machines, party/kids room, kitchen, 2 WC, office, storage, mechanic room |
| Large | 24-40+ | 2,200-3,700 m2 | restaurant, big bar, arcade 30+, billiards hall 8+ tables, VIP/boutique zone (movable wall, 6-8 lanes), party rooms, conference, pro shop, often a mezzanine |

- **Mezzanine** from 24 lanes: restaurant or bar above the concourse/settee side, glass or railing at
  1.0-1.1 m overlooking the lanes; stairs and lift 3 m wide near the entrance. Lanes stay on one floor.
- **Dream-Bowl Palace Munich** (52 lanes, ~8,000 m2, 3 floors): floor 1 lanes + bistro + lounge, floor 2
  restaurant, conference, billiards over the lanes, floor 3 mini-golf and pro shop; 8-lane boutique
  zone with movable wall [S]. Mall-basement centres exist but need the 27 m free length.

## Rooms and fittings [E unless marked]

- **Front desk / shoe counter**: 3-5 m long, 0.9 m deep, 1.05 m high pass-through; shoe wall behind,
  cubbies 0.3 x 0.35 m in a 2.2-2.6 m wall, 100-250 pairs for 12-24 lanes; size numbers on the heel;
  return shelf under the counter. Shoes two-tone red/blue/brown.
- **Bar**: counter 4-10 m, 1.1 m high, footrail; stools 0.35 m dia x 0.75 m every 0.6 m; back bar 0.35 m
  deep, 2.2 m high, mirror, shelf lights, taps, cocktail neon. Tables 4-seat 0.8 x 0.8 m, booths 2.0 x 1.5 m.
- **Kitchen** 15-40 m2 behind the bar; **WC block** 15-30 m2; **party/kids room** 25-40 m2 with a round
  table for 8-12; **office** 10-15 m2; **storage** 15-30 m2; **mechanic room** 15-25 m2 at the corridor end.
- **Settee**: curved bench 2.0-2.4 m long, 0.55 m deep, seat 0.45 m, back 0.85 m, vinyl teal/red/orange/
  black/purple; table 0.7-0.9 m, 0.75 m high (round, or console on a pedestal). ~6 seats per pair, two
  facing benches around the table; own bay per lane, 1.0 m low divider.
- **House ball rack** 2.4 x 0.5 x 1.1 m, two tiers, 20-40 balls by weight, on the concourse wall or at
  each hood. Ball colours: black, blue, red, green, purple, orange, pink, yellow, swirl; 3 dark finger
  holes; weight in white numerals.
- **Bumpers** (kids): rails 0.1 x 0.05 m filling the gutters foul line to pins, pink/blue/lime.
- **Pool table** 9 ft: surface 1.27 x 2.54 m, frame 1.4 x 2.7 m, height 0.8 m, green or tournament blue
  cloth, dark wood [S dims]; 8 ft surface 1.12 x 2.24 m. Room with a 1.47 m cue: 8 ft ~4.1 x 5.2 m,
  9 ft ~4.3 x 5.5 m, tournament 4.6 x 6.1 m min [S]. Hall grid 4.3 x 5.5 m; lamp 1.8 x 0.6 m at
  0.95 m over the cloth; wall cue rack 1.2 x 1.5 m (6-8 cues) every 2-3 tables.
- **Darts**: oche 2.37 m, board centre 1.73 m, lane 0.9-1.0 m wide, 3.2 m deep. **Foosball** 1.4 x 0.75 m,
  **air hockey** 2.1 x 1.2 m, 3-4 m2 each with clearance.
- **Carpet** (concourse, settee): random neon shapes (triangles, circles, squiggles, planets) on dark navy/
  black/teal; machine area bare concrete. **Neon**: "STRIKE", "BOWLING", "BAR", pin-and-ball in red/pink/cyan.

## Lighting and sound

- **Normal mode**: warm white, 300-500 lux in the concourse, lanes 700-1,000 lux (pin deck and approach
  lit), settee lower and warmer [E].
- **Cosmic / glow mode**: house lights to 5-10 %, UV blacklight tubes along the ceiling and over the
  pin decks; arrows, gutter edges, pins, balls, masking graphics and carpet fluoresce; moving heads,
  mirror ball, laser/LED, fog; lane edge LED strips. Flon Bowl Bar lights its lanes in changing
  colours pulsed to music [S].
- **Mode by time**: weekday afternoons normal and quiet (seniors, leagues, school groups); evening and
  weekend (Fri/Sat 20:00-02:00 at Zurich and Vidy) dim + disco; Sunday family hours bright, bumpers on.
  League nights: teams in matching shirts. Hours: La Praille 11:00-00:00; Vidy opens 15:30 on weekdays [S].
- **Sound loops**: constant ball rumble (~60-100 Hz), a pin crash every 2-6 s per active lane, pinsetter
  cycle (whir, clunk, rake) after each ball, return "thunk", gutter thud, cheers, bar noise; pop/rock
  1980-2000 hits at conversation volume, louder dance in disco mode [E].

## Exterior

- **Box**: windowless big box 40-70 m long, 18-45 m wide, 6-9 m high, flat or shallow pitched roof, metal
  panel or rendered block; windows only at the entrance and restaurant [E]. In commercial/industrial
  zones on a main road (Dubendorf, Dietlikon, Dielsdorf, Wetzikon, Schlieren [S]), in mall basements,
  or attached to a cinema (CineBowling [S]).
- **Entrance**: glazed double door, flat canopy 3-4 m wide x 1.5-2.5 m deep lit from below; rear delivery door [E].
- **Sign**: roof sign or pylon 6-12 m, "BOWLING" + name in bold sans or retro script, a **pin and ball
  icon** (white pin, two red stripes, ball, burst), red/blue/yellow on white or neon/LED. Swiss ones are
  plain (roof sign, banner, lit vertical strip). Googie centres: up-swept roof, tall neon pylon,
  cantilevered canopy, zig-zag walls (Covina Bowl 18 m pylon, Kona Lanes tiki) [S]. Window vinyl with
  pin silhouettes, "OPEN" neon.
- **Parking** in front, asphalt, 5 spaces per lane (60-150 at 24 lanes), bike racks, bus stop [S/E].

## Swiss and nearby examples

| Name, town | Lanes | Extras |
|---|---|---|
| Bowling Center Zurich, Glattalstrasse 40, Seebach | 24 | bar + restaurant [S] |
| Bowling West, Schlieren | 16 | billiards, table football, darts [S] |
| OZ Bowling, Dielsdorf | 14 | billiards, darts, table tennis, small arcade [S] |
| Joe's Bowling Center, Wetzikon | 16 | billiards, darts, Italian restaurant [S] |
| PrestigeBowling, Dubendorf | 12 | billiards, darts [S] |
| Bowling Center Marzili, Bern | n/a | renovated, cafe/bar, party events [S] |
| CineBowling (Blue Cinema), Gumligen, Biel, Emmenbrucke, Winterthur | n/a | bowling attached to a cinema, sports bar [S] |
| Bowling La Praille, Geneva | 26 | 8 pool tables, ambient bar, table tennis, many video games [S] |
| Bowland Vidy, Lausanne, Route de Chavannes 27C | 14 | 3 pool tables, table tennis, darts, video games, VR, snack bar [S] |
| Flon Bowl Bar, Lausanne | 10 | colour-pulsing lanes, video games, foosball, popcorn, full bar [S] |
| Dream-Bowl Palace, Munich | 52 | see above [S] |

Swiss default: 12-24 lanes + billiards/darts + restaurant around Zurich; Romandie "bowling + billiards +
games". Lane counts come from directory pages and may be stale; no real floor areas found.

## Sources

- Lane and pin dimensions: https://www.dimensions.com/element/bowling-lane , https://www.topendsports.com/sport/tenpin/dimensions.htm ,
  https://dimensions.com/element/ten-pin-bowling-pin , https://en.wikipedia.org/wiki/Tenpin_bowling
- Planning: https://strikerbowling.com/wp-content/uploads/2017/05/Preplanning-Guide.pdf (Brunswick, snippets) ,
  https://www.bowling.com/braden.aspx
- https://www.amusementsandattractions.com/amusements/news/dream-bowl-palace-opens-in-munich
- Swiss centres: https://switzerlanding.com/bowling-zurich/ , https://zuri.net/en/zurich/sport/bowling-center-zurich-8707.htm ,
  https://www.lausanne-tourisme.ch/en/explore/vidy-bowling/ , https://torpille.ch/listing/bowland-vidy/ ,
  https://torpille.ch/listing/bowling-la-praille-carouge/ , https://www.loisirs.ch/activite/bowling-de-la-praille/ ,
  https://www.timeout.com/switzerland/things-to-do/bowl-bar-flon ,
  https://www.kupi.com/en/explore/switzerland/bern/the-bowling-company-ag , https://faq.bluecinema.ch/hc/en-150/articles/31581957974931-Bowling
- https://patents.google.com/patent/US5529541A/en , https://patents.google.com/patent/US5846138A/en (glow bowling) ,
  https://en.wikipedia.org/wiki/Covina_Bowl , https://en.wikipedia.org/wiki/Kona_Lanes
  https://billiardfactory.com/how-much-room-do-you-need-for-a-pool-table , https://www.sawyertwain.com/blog/how-much-space-do-you-need-around-a-pool-table/
