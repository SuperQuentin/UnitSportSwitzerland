# Entertainment venues (epic #1232)

Bowling centres, cinemas, arcades, restaurants, cafés and bars where they really are, with interiors,
exteriors and ambience built for each. This epic is generation only: playable lanes, pinballs and
arcade games, and shared movie nights are a later epic, and every plan leaves room for them (each
machine its own piece with a stable index, each screen and light bank its own node).

## Decided with the owner (2026-10-26)

- Signs show the venue's real OSM name in neon with a generic icon, never a logo.
- Music: the server's live web radio relay, a station fitting the venue; offline or without the relay,
  tracker modules (XM/MOD/IT) under free licences, played by a small player of our own.
- People: staff only (cashier, bartender, popcorn seller, shoe-counter clerk), skinned figures idling.
- A PR per part, merged by the worker once finished and green.

## Parts

1. **Data and detection** (#1233, PR #1234): `--venues` stage -> embedded `venues.tsv.gz`, matching by
   footprint (`Venues`, `VenueOf`, `DoorSpot.Venue`), neon door signs, `--venuecheck`, research notes.
2. **Bowling**: lanes in pairs (3.50 m a pair, 27 m lane block with the pinsetter room), settees and
   score screens, concourse, shoe and ball counter, bar or pizzeria, pool tables, darts, an arcade
   corner with a pinball, WC; a mezzanine over the lanes when the building has storeys; cosmic light;
   a big facade sign with pin and ball. Where the solid is too small for lanes, fewer lanes, then a
   bowl bar (as Lausanne's Flon has).
3. **Cinema**: lobby (box office, concession stand, posters, standees, WC), at least one full-size
   auditorium (stadium rows by the sightline rule, screen, curtains, step lights, exits), more as the
   solid allows; a projection room up a stair with the control panel: house lights and the screen
   (a cat picture on or off), server-owned and synced, keyboard, pad and VR, callable by bots;
   marquee and poster cases outside.
4. **Arcade**: uprights, racers, simulators, light-gun, dance, air hockey, basketball, claw machines,
   pinball rows, change machine, prize counter; dark room, neon, attract screens in a shader.
5. **Dining**: restaurants, cafés, bars, pubs as ground-floor units under flats (several per city-block
   solid: `Venues.Contained` lists them all) or whole small buildings; kitchen, bar, dining, WC; bar
   games by odds (pinball, table football, darts, a stage); terraces on the pavement in front.
6. **Music**: speakers per venue on the radio relay, heard through walls by `Hearing`; the tracker
   fallback.

Each part: rooms and pieces in the model viewer, `RoomLights` rows (Cartoon first), synthesised
ambience in the sound player, staff figures, loot where it fits, its own `--...check`.

## Shape of the code

- A venue is a property of a solid (`BuildingTypeMap.VenueOf`), not a `BuildingGroup`; each part's
  planner branches in `InteriorGenerator.Generate` the way IKEA and industrial sites do, bumping
  `InteriorLayout.CurrentVersion` when it changes plans.
- A hall venue (bowling, cinema, arcade) in a solid far bigger than its programme (a mall) plans its
  programme in a block at the entrance side and leaves the rest as the generic hall.
- Facades: a hall venue's walls lose the window grid where its halls have no windows, and the
  interior cuts none there either (the IKEA rule).
- Research: `docs/notes/terrain/venue-*.md`.
