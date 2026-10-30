# Pinball: the real conventions, and the four tables (#1325, epic #1322)

Research for playable pinball. The four tables (`src/Interiors/Pinball/PinballLayouts.cs`) are original
designs that follow these conventions; they copy no real machine. Reference pictures stay out of the repo.

## Sizes [S]

- **Playfield** 20.25 x 42 in (51 x 107 cm) under a 21 x 43 in glass (53.3 x 109.3 cm, 3/16 in thick) — the
  standard since the 1960s; widebodies 60 cm. Slope **6.5°** away from the player (operators steepen it for a
  faster game). Gravity along it: g sin 6.5° = 1.11 m/s²; a rolling ball feels 5/7 of it, 0.79 m/s².
- **Cabinet** 131.5 cm long, 56 cm wide; 40 cm high at the front and 60.5 cm at the back (without legs);
  lockdown bar 90-92 cm from the floor, flipper buttons 85-87 cm. **Backbox** 73 cm wide, 73 cm high, 25 cm
  deep; its top 193-197 cm. Machines side by side need ~75 cm each: the venues' pieces are 0.75 m wide.
- **Ball** 1 1/16 in (27 mm) steel, ~80 g.
- **Flippers** 3 in (7.6 cm) overall since the early 70s (2 in before), tapered rubber bats. We take a 25 mm
  base, 13 mm tip, 58 mm pivot to tip; at rest 28° below level, a 50° stroke; tips 7.4 cm apart centre to
  centre (about 6 cm of gap: "as close as possible without the ball being able to pass" is the design rule,
  real games leave a narrow centre drain).

## The lower third (every table)

From the side wall in: **outlane** (to the drain), a separator with a post at its top, **inlane** (feeds the
flipper), the **slingshot** (a triangle of rubber whose inner face kicks when hit), then the flipper. The
inlane guide ends just above the flipper's base, so a ball rolling down it lands on the bat and a raised
flipper **cradles** it in the corner. Ball save, kickbacks and post saves exist for the outlanes; we use the
ball save. Shoot-again insert between the flippers; bonus multiplier inserts above.

## Shots

- **Flipper shots are timing**: a cradled ball released and flipped late leaves near the tip early in the
  stroke (a cross shot, to the far side); flipped early it rides the bat and leaves later in the stroke (a
  steeper shot up the near side). Our solver measures 70-112° (left flipper) from a cradle and 56-88° from
  balls rolling down the inlane: each flipper covers the centre and the far side, the convention.
- **Orbits** (loops): a lane along a side wall over the top and down the other side; spinners often sit in them.
- **Ramps**: plastic inclines the ball climbs off the playfield, often handing over to a wire form that brings
  it back to an inlane (a made ramp feeds the flipper). Too slow, the ball rolls back out of the mouth.
- **Scoops / saucers** catch the ball and kick it out; a **subway** carries it under the playfield to come up
  elsewhere (a VUK, the plunger lane).
- **Pop bumpers** (about three, usually up top under the lanes) kick when the ball touches the skirt.
- **Drop targets** fall when hit, in banks that reset when cleared; **stand-ups** stay up.
- **Top lanes** (rollovers) with **lane change** on the flipper buttons: completing them raises the bonus
  multiplier. A soft plunge into a lit lane is the **skill shot**.
- **Plunger**: the pull sets the launch; one-way gate at the shooter lane's top.

## Rules vocabulary (modern-lite, `PinballRules`)

Missions (modes) started at a lit scoop, a timer and lit shots; multiball from locks (virtual: the ball comes
back out), jackpots during it; ball save (the first seconds of a ball and of a multiball); extra ball
("shoot again"); combos (major shots in quick succession); bonus x multiplier counted at the drain; a wizard
mode once every mission is done; **tilt**: a plumb bob in a ring, warnings ("danger") then the flippers die and
the ball drains without bonus; **match** at the game's end: the last two digits against a random 00-90, a free
game (default ~7 % on modern games, nominally 1 in 10), announced by the **knocker**.

## Eras of the backbox

1950s-70s electro-mechanical: **score reels** behind a painted, lit backglass; bells and chimes (10, 100,
1000). Late 70s-80s solid state: **7-segment**, then alphanumeric displays. 1991-2010s: the orange 128 x 32
**dot-matrix** display (DMD). Since 2010 (Europe) / 2013 (US): **LCD**s.

## The four tables

| Table | Era | Ramps | Orbits | Toys |
|---|---|---|---|---|
| Clockwork (watchmaker) | reels, chimes | Escapement (left, wire form to the right inlane) | both, the mainspring spinner left | 3 hour drops, 3 dial stand-ups, the watch-case scoop, gear bumpers |
| Space Mission | 7-segment | Launch ramp (right, wire form to the left inlane) | left, thrusters spinner | 4 planet drops, 2 beacons, the airlock subway to the plunger |
| Haunted Castle | dot-matrix | Staircase (centre, wire form to the left inlane) | right, bat-wings spinner | 3 tombstone drops, 2 spirit stand-ups, the crypt subway |
| Alpine Ski Race | LCD | Downhill (left → right inlane) and Slalom (right → left inlane), crossing | both, chairlift spinner | 4 slalom gates, the start hut scoop, mogul bumpers |

Shots are laid out on the solver's measured fans; `PinballSolverTests.Every_shot_is_reachable_from_the_flippers`
proves each table's every shot from some flipper timing. `PINBALL_PNG=<dir> dotnet test --filter Draw_tables`
draws each table and its cradle shots (1 mm a pixel) for laying out a new one.

## Sources

- flippers.be, "Dimensions of Bally Williams pinball machines": https://www.flippers.be/basics/101_pinball_dimensions.html
- Wikipedia, "Pinball" (slope, flippers, slings, bumpers, ramps, spinners, drop targets, tilt, match, displays): https://en.wikipedia.org/wiki/Pinball
- Wikipedia, "Glossary of pinball terms": https://en.wikipedia.org/wiki/Glossary_of_pinball_terms
- Lysator pinball glossary: https://www.lysator.liu.se/pinball/glossary/
- Flipper spacing as a design rule: US patent 4971324, "Variable position flipper mechanism": https://patents.google.com/patent/US4971324
- designingidea.com, flipper sizes (3 in since the 70s, 2 in before): https://designingidea.com/pinball-machine-dimensions
