# Falling through the world: the void rescue

- **`FootPlayer.RescueFromVoid`** runs at the top of every physics step, before any path
  (on foot, mounted, flying, mantling, a thrown-out NPC), so none can skip it (#150). It replaced
  `ClampAboveTerrain`, which only ran at the end of three of those paths and only where the terrain
  height was known.
  - **Outdoors, more than 2 m under known terrain**: straight up onto it, same X/Z.
  - **Outdoors, below `VoidY` (-500 m) with no height known** (tile not streamed, no data): back
    to the last safe spot, or held at the same X/Z until the ground there streams in.
  - **Indoors, 10 m under `InteriorManager.InteriorBaseY`** (-3000 m, where every interior's
    ground floor is): back to the last safe spot in that interior; with none, out to the street.
- Every rescue goes through `RequestReplacement`, like a teleport: stopped, no fall damage
  charged, set down by the placement pass once the ground has collision.
- **The safe spot remembers its space** (`_safeSpace`: the interior key, or null outside).
  `HasSafeHere` is false across spaces, so neither a rescue nor a knock-out `Revive` can drop an
  outdoor player 3 km under the street into an interior, or the reverse — a teleport out of a
  house (`InteriorManager.Leave`) records no new safe spot.
- Check: `<godot> --headless --path . -- --voidcheck [--at E,N]` (`VoidProbe`) drops a body 30 m
  under the terrain on foot and on a bike, 600 m down where no tile is loaded, and under an
  interior floor; non-zero exit if any does not end back on the ground. On `main` before #150 the
  void and interior cases fail. Works offline on the generated terrain, no chunks needed.
