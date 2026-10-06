# Fishing: rod, real Swiss fish by basin and lake, the catch rules (#493)

`src/Items/Fishing/`. Pure rules (unit-tested, linked into the tests): `FishCatalog` (species),
`FishWaters` (basins, lakes, water kinds), `FishRules` (who bites, size, the law, `FishFight`). Game side:
`FishingRod` (the local cast/bite/fight, HUD), `FishingVisuals` (float and line on every peer),
`FishJournal` (the catch book), `FishProbe` / `FishNetProbe` (checks).

## Controls (keyboard / gamepad / VR, `general/new-action-three-devices`)

All on the existing `use_item` / `aim_item` (no new action): LMB/RMB, RB/LB, VR right/left trigger (R2).

| Step | Keyboard | Pad | VR |
|---|---|---|---|
| Wind up a cast (hold), cast (let go): 3-25 m along the view | hold LMB | hold RB | hold R trigger |
| Strike when the float dips (1.1 s) | LMB | RB | R trigger |
| Reel (hold); let go when the line strains or the fish runs | hold LMB | hold RB | hold R trigger |
| Wind the line in / cancel a cast | RMB | LB | L trigger |

Prompts through `InputHints` (`ClientWorld` hints per phase, the HUD's "A bite! {use_item}"), F1 rows
(`ControlsHelp`, "Fishing"), a row in `xr/vr-action-map` (**gap**: flick the rod hand to cast, crank the
reel with the left hand, R1/R4). Bites and runs rumble the pad (VR: the hands, `PlayerInput.Rumble`).

## The loop

- **Cast** (`FishingRod.Cast`): the float lands on the water layer (`WaterField.TryGetStill`, with at least
  `MinDepth` 0.15 m over the ground found by a ray: the layer runs on under a beach) or, with no surface, in a
  mapped stream line (`Gathering.StreamAt`, the watercourse segments); else on dry ground, and comes back.
- **Where** (`FishWaters.Spot`): basin from LV95 (`BasinAt`: lines along the watersheds, checked against 32
  towns in `FishingTests`), a named lake by centre, reach and **level ±12 m** (Lake Zug 413 m vs Lucerne
  434 m where their reaches overlap), the kind from the surface's slope (> 0.2 % = a river), altitude
  (> 1500 m still = alpine lake, a stream line > 1000 m = mountain stream). Nothing on disk names a water
  body or tells a lake from a river (the water layer keeps only level and wave scale): that is why.
- **Bait** from the pack: a spinner first (hunters: pike, perch, trout, zander, char; lost only on a snap),
  else dough (carp family, whitefish; eaten by each fish that takes it or a missed bite), else a bare hook
  (slow). `FishRules.Weight`: rarity × known-in-this-lake ×3 × bait × night (wels, zander, burbot, eel ×2.5).
- **Bite** after an exponential wait (`BiteSeconds`, mean 14 s, ×0.65 at dawn/dusk, ×1.25 at midday,
  ×2.5 bare hook, ×1.5 on a stormy lake); `WorldClock.CurrentHour`. Use within 1.1 s strikes.
- **Fight** (`FishFight`): pull from the weight (0.1 a minnow .. 0.95 a wels); reeling pulls the tension
  toward 1.2 × the pull, letting go eases it while a strong fish takes line; surges every 2-5 s from fish
  over 0.5 kg, **warned 0.6 s ahead** (HUD red, "It runs! Let go!", rumble). Tension > 1 or 45 m of line
  out: snapped. Within 1.5 m: landed. A tight line tires the fish.
- **The law** (`FishRules.Judge`, real rules): protected (Red List CR, the extinct salmon and huchen)
  always released; closed seasons by month (`BirdLife`'s `--birdmonth N` overrides, like hunting);
  federal/cantonal minimum lengths; **the Lake Constance whitefish ban 2024-2026**; the invasive
  stickleback killed and thrown away. Released fish give nothing but go in the catch book.
- **Kept**: one item per species (ids 200-222), Food, `Consume` raw (+8); the local whitefish name
  (palée, Albeli, féra...) in the toast. The size is not stored on the stack (plain stacks only, so
  crafting works): it goes in `FishJournal` (`user://fishing.json`, best length/weight/where per species).
- **Remote peers** see the float and line (`ItemEventKind.FishCast` 9 / `FishEnd` 10, sent with no
  direction so the position stays; protocol 17) and a kept fish flying to the angler. The bite and the
  fight are the owner's only. A float goes when the angler's replicated held item is not the rod.

## Data (`FishCatalog.All`, 38 species, `FishWaters.Lakes`, 17 lakes)

Sources: BAFU / info fauna Red List 2022 (status); VBGF SR 923.01 art. 1-2 (closed seasons, minimum
lengths), Zug, Bern and Léman rules (raised sizes, dates); CSCF atlas (ranges); Eawag / Projet Lac
(each lake's whitefish species); Léman fishers' prices (perch 47-55 CHF/kg, char 45, whitefish 25-30,
pike 20-22) set the item values relative to each other. Lake centres are approximate (±2 km).

## Economy and crafting

- Rod: workbench (2 planks, rope, scrap, 2 screws), sport shops (35 CHF), rare in barn crates
  (`LootTables.FishingGear` with the spinner). Dough bait: by hand, 1 bread → 4; sport shops. Spinner:
  workbench, 1 scrap + 1 screw → 2.
- Cooking (Fire): 2 perch → **filets de perche** (heals 60); 2 of the carp family + mineral water →
  **fish soup** (45); anything else → **grilled fish** (50). One cook row per fish, shown only while that
  fish is in the pack (`Recipe.OnlyWhenHeld`). Every dish is worth more than what goes in (`--invcheck`).
- Groceries buy fish back (Food, 35 %) and sell perch and whitefish at the fish counter.
- The field journal (`J`, the wrist menu in VR) has a **Fish** page; the rod in hand opens it there.

## Checks

- `tools/test.sh unit` (`FishingTests`: basins of 32 towns, lakes by level, water kinds, the law, who
  bites where and at night, bite times, lengths, the fight (a perch on a held reel, a wels snaps a bullied
  line and comes in played), the recipes).
- `--invcheck` (items, icons, values), `--iconsheet`, `--shopcheck`, `--lootchancecheck`.
- `--fishcheck --chunks fixture:lake` (quick, offline): dry cast, a 25 m cast onto still water, a bite
  struck, the fish played and landed, Aim and putting the rod away wind in, a river cast.
- `tools/fishcheck.sh` (net, two clients on the lake fixture): B sees A's rod, A's float where A's is, and
  loses it on A's wind-in and when A puts the rod away.

## Not done (follow-ups)

- VR rod gestures (flick to cast, crank the reel): the triggers work, the gestures are a gap.
- Fishing from a boat or a pier deck (on foot only, like every item; a deck counts as on foot).
- A permit (Patent / SaNa), daily bag limits, PFAS/PCB sale bans: researched, not played.
- Water body names from swissTLM3D (needs the preprocessor to keep them); seasons from a game calendar.
