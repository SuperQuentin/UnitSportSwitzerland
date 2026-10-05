# Swiss match items: alphorn, fondue pot, smoke canister (#478)

`Items/SwissItems.cs`. Found as Battle Royale loot (`MatchLoot` supplies: horn 2, fondue 3, smoke 5;
a smoke canister in half the military crates); they work in free roam too (`/spawn`). Never crafted.
Each is an item event (`ItemEventKind.Horn` 6, `Fondue` 7, `Smoke` 8) the owner sends and the server
relays like the flare (to the peers that see the owner, within 30 m of its body); every peer plays its
own part, nothing is decided on the server.

- **Alphorn** (`ItemUse.Horn`, not used up): a 2.8 s blow (`StartUse`, switching cancels), then 30 s
  out of breath. Everyone who hears it gets the call (`SwissItems.Horn`, B♭2 with overtones, heard
  1.5 km) and, in a match, an orange ring on the maps and an "alphorn" compass marker (row 5) where it
  was blown, for 8 s (`BrManager.HeardHorn`/`Horns`). The blower's own radar reaches 120 m (not 80)
  and sees through camo nets, hay hideouts and smoke for those 8 s (`BrManager.Nearby`).
- **Fondue pot** (`ItemUse.Share`, stack 2): a 5.5 s hold, then the pot is set out: every peer whose
  own player stands within 4 m (and can use items) eats 40 health, user and enemies alike
  (`SwissItems.OnFondue`, health is the owner's). Bubbling sound, a warm light.
- **Smoke canister** (`ItemUse.Smoke`, stack 3): thrown 15 m along the aim (`ItemController.AimFrom`,
  down to the ground if it hits nothing). A cloud of 16 grey unshaded spheres, 10 m across, 12 s, on
  every peer; `SwissItems.InSmoke(GlobalPos)` (LV95, origin shifts are safe) hides whoever is inside
  from the minimap radar. It does not block shots or the server's line of sight.
- Icons authored in `ItemIcons` (horn, red caquelon, canister); held meshes in `ItemDefs.HandMesh`.
- Check: `tools/swisscheck.sh` (two clients, flat fixture): A's fondue feeds A and B (+40 each), A's
  canister lands on B (B is in the cloud on its own peer), B hears A's horn.
