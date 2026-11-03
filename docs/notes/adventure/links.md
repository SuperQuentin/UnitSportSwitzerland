# Links: holding hands, the rope, two-player routes and crevasses (#1266)

`LinkBook` (pure, `LinkMode { Hand, Rope }`) lives on the server; `TogetherService.Links` mirrors it on every peer (the `Links` RPC to all), because everyone draws the hands and the rope.

- **Ask**: an invite of kind `rope` (hold E at a party member, `/rope [hands] <player>`, `/rope off`). `ServeLink` and `ServeUnlink` hold the rules; a bot calls the same.
- **Mode**: Hand pays out to Rope by itself on glacier or snow cover, climbing, swimming or more than 1.6 m apart for 1 s; back to Hand on easy ground within 1.2 m. "Rope up" forces Rope.
- **Breaks** (server tick, `LinkRules`): far apart for 3 s, a vehicle of one's own, leaving the party or server, `Active` false (a Battle Royale lobby).
- **Tether** (`Tether`, pure): applied to the walk intent in the owner's physics step, so a bot's `WalkControls` goes through it. Hand: a follower with no input is led along the leader's trail (ring of 32 points, 1.1 m behind). Beyond the length the outward part of the wish is removed and a pull added.
- **Stamina**: `StaminaCompany { None, Linked, Belayed }`: regen ×1.2 linked, 6 /s belayed. A roped fall is held at the rope length, then hangs; the partner hauls (`AskHaul`).
- **What people see**: `ArmHold` (an arm override made of bones, from the book and both bodies' replicated positions) and `RopeNode` (one shared tube mesh stretched by its transform; the sag is a shader instance uniform). Nothing rebuilds a mesh.
- **Two-player routes** (`TogetherService.Routes`): a leg-up (crouch facing a wall, the partner jumps for a free 2.6 m), the two-handle gate (`GateMeshes`, `AskHandle`/`ServeHandle`, both wheels within 1.5 s), `SealKind.Roped` chests (a roped pair within 8 m).

## Crevasses

Hidden glacier crevasses (the slot, the fall, the rope catch, the climb out, `PoseKind` 9) are in `crevasses`. `Crevasses` (pure, tier 0): 12 m cells of glacier cover, 4 % hold a hidden strip 1.5 m wide.

`--together-glacier E,N,R` (any peer) pretends glacier cover in a box, for checks on the fixture.
