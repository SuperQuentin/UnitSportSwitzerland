# Appearance (#394): who a figure is, chosen, packed, replicated, or seeded

- `Avatar.Appearance` = build, face, eyes, skin tone, hair style, hair colour. Indices into
  `SkinTones`, `EyeColours`, `HairColours` and the `BodyBuild` / `HairStyle` enums: **append
  only**, a packed value must mean the same figure on every peer.
- `Pack()` puts it in one int with bit 30 set ("chosen"); `Unpack(0)` is null = nobody chose.
- The player picks it in the inventory's Body row (`InventoryUi.BuildBodyRows`, ‹ › per part),
  saved as `GameSettings.AppearanceBits`. `Occasions.OccasionHats` (owner, every 0.25 s) writes it
  to `FootPlayer.AppearanceBits`, replicated OnChange like `OutfitBits`. `RefreshVisual` rebuilds
  the visual when it changes (its early-out compares it); the passenger cache keys on it.
- **Rides need no new parameter**: `FootPlayer.RefreshVisual` calls
  `Appearance.Register(rider, AppearanceBits)` (on every peer) before building, and
  `HumanPalette.ForRider(index)` takes `Appearance.For(index)`: the chosen one, else
  `Appearance.ForSeed(index)`. So every `BuildVisual(riderIndex, outfit)` draws the rider's figure.
- Who never chose gets `ForSeed` of their rider index (the peer id), the same on every peer;
  masculine builds draw from the masculine faces and hair. NPCs use `FootPlayer.RiderIndex()`:
  their owner's id offset by 100 × a slot from the NPC id, so they do not wear their owner's
  chosen figure. GPX ghosts are seeded from their tint (`Gpx/Runner`, `Cyclist.CreateWithTint`).
- `HumanPalette.With(appearance)` sets the appearance and the skin tone together (`Skin` stays a
  palette colour, read by the body).
