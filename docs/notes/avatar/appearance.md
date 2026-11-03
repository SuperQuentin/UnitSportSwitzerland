# Appearance (#394): who a figure is, chosen, packed, replicated, or seeded

- `Avatar.Appearance` = build, face, eyes, skin tone, hair style, hair colour. Indices into
  `SkinTones`, `EyeColours`, `HairColours` and the `BodyBuild` / `HairStyle` enums: **append
  only**, a packed value must mean the same figure on every peer.
- `Pack()` puts it in one int with bit 30 set ("chosen"); `Unpack(0)` is null = nobody chose. The
  layout is `AppearanceCode` (plain C#, unit tested): build 0-2, face 3-6, eyes 7-9, skin 10-12, hair
  13-16, colour 17-20, then face bit 21 and skin bits 22-23 (#880, the aliens: `aliens`).
- The player picks it in **the Character menu** (#806, `Ui/CharacterPage`: one option row per
  part beside a turning preview, an own-world SubViewport built only while the tab is on screen;
  it was the inventory's Body row before, whose ‹ › saved without telling anyone). A change sets
  `GameSettings.AppearanceBits`, fires `GameSettings.Changed` and saves that key alone
  (`SaveOnly`: no command-line override is saved). `ClientWorld.PublishAppearance` writes it to
  the local `FootPlayer.AppearanceBits` on that event and every 0.25 s (a fresh spawn, a probe's
  in-memory set), whatever systems run; `OccasionHats` no longer touches it. Replicated OnChange
  like `OutfitBits`, relayed by the server unchanged. `RefreshVisual` rebuilds
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
