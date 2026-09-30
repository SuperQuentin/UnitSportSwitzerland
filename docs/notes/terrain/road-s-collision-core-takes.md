# A road's collision core takes the height at the cell's perpendicular foot on the centreline, nearest segment wins

- **A road's collision core takes the height at the cell's perpendicular foot on the
  centreline, nearest segment wins** (`ComputeRoadBlend`). Letting the last stamp win, or a
  neighbouring road's fade-out pull it, left 0.1–0.4 m of scatter (feet sinking into one path,
  hovering over the next); stamps are 1 m apart, which on a 30% alpine path is 15 cm by itself.
  The core is at least one lattice spacing wide, or a 1.2 m footpath can miss every corner of the
  quad its centreline crosses. Check with
  `<godot> --path . -- --roadcheck [--bridges] [--at E,N]`: drops a body on real road (or bridge
  deck) points and prints ribbon vs floor vs body, non-zero exit if it rests >5 cm below.
