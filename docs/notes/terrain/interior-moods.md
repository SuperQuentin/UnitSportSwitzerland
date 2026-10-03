# Interior moods: fancy, lived in, messy, abandoned (#434)

- **The roll.** `InteriorLayout.Mood` (`InteriorMood`, stored in the plan, plan v14):
  - `InteriorGenerator.MoodOf` rolls it from `key|mood`, so a house's rooms and furniture do not
    move whatever its mood.
  - Probabilities (fancy / messy / abandoned; lived in is the rest):

    | Kind | Fancy | Messy | Abandoned |
    |---|---|---|---|
    | House, town | 17 % | 16 % | 7 % |
    | House, countryside | 10 % | 22 % | 14 % |
    | Apartment | 6 % | 16 % | 4 % |
    | Other | 8 % | 16 % | 8 % |
    | Farm | — | 30 % | 12 % |
    | Annex, garage | — | 25 % | 12 % |

  - Banks, shops and everything else are always lived in.
- **Fancy**:
  - **Double-height living room** in 3 of 4 fancy houses with 2+ storeys (`Tall`, `key|tall`).
    `TryCored(tall: true)` runs first, from the same seed. The ground floor's living room (else its
    dining room) takes `Span = 2` when it spans its side's whole width (`Lofty`). The storey above
    plans its rooms only before and behind that rectangle (`AroundLofty`, leftovers under 2.4 m
    stay empty). If that fails, the plan is redone as usual, entry position restored. `ClearOf`,
    `RoomLights` and `InteriorValidator` already handle `Span` (the church nave).
  - **Extra pieces** after a room's own (`MoodPieces`):
    - living room: a fireplace, two paintings, a tall plant and a big rug;
    - bedrooms: a painting and a mirror;
    - hall: a mirror and a painting;
    - study: a painting;
    - bathroom: a mirror.

    Paintings and mirrors are planned 2 m tall so they keep out of windows (`TryPlace`), but drawn
    high on the wall, with no collision.
  - **Looks**: cornices round grand rooms (`Grand`), and the house's wall colour in its living,
    dining, study and bedrooms: one of cream, sage, burgundy or navy, from `key|walls`.
- **Messy** (`Neglect`, its own seed `key|clutter`, after `Secure`): per room by type,
  - clothes piles (bedrooms, bathroom, living, hall, laundry);
  - papers (study, office, living, bedroom);
  - bottles (kitchen, living, carnotzet, cellar, cinema);
  - trash, a dirt patch, and now and then a cobweb.

  Bare bulbs in half the rooms. Every colour a little dulled (`Weather`).
- **Abandoned**:
  - about half the furniture that is not `Kept` is gone (beds, sofas, tables, sanitary, kitchen,
    built-in, locked and sold pieces stay);
  - cobwebs in most upper corners, 2–4 dirt patches per room, trash, papers, bottles, a plank, a
    fallen chair;
  - no lamp works (`RoomLights.Lit`: only the windows light it), only frayed cords hang;
  - every colour greyed and darkened.
- **Clutter** (`Scatter`): new `FurnitureType`s on the end of the enum (`Painting … Plank`).
  - Placed anywhere free on the floor and never in a doorway's clearance; a dirt patch ignores
    other pieces. No collision (`InteriorMeshBuilder` skips every type from `Cobweb` on).
  - The validator treats `DirtPatch` like a rug.
  - A cobweb is a square piece in a corner at the ceiling (`Lift = clear - H`), drawn as
    double-sided sheets from the corner.
- **Lamps** (`RoomLights.LampOf` / `LampAt`, drawn by `InteriorMeshBuilder.Fixture` at the light's
  own spot):
  - cloth shade by default;
  - brass chandelier in a fancy house's grand rooms, two tiers hung 1.5 m down in a double-height room;
  - bare bulb in cellars and half of a messy house's rooms;
  - cord only when abandoned;
  - light panel in offices, shops, schools and banks;
  - enamel shade in workshops and stores;
  - nothing drawn in a church.

  Never lower than 2.15 m off the floor. No collision.
- **Check**:
  - `--lootstats` (real terrain) prints the mood mix per kind, double-height rooms against fancy
    houses with 2+ storeys, and pieces, clutter and art per house by mood. It writes SVG plans of
    double-height and abandoned houses to `test_output/rooms/`.
  - `--moodshots [dir]` (windowed, real terrain, `--style`) photographs a fancy, lived, messy and
    abandoned house, a cinema, a music room and a running tap into `test_output/moodshots/`.
