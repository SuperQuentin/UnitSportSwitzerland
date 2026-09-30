# Player scale is set by speed, not by size

- **Player scale is set by speed, not by size.** A 1.8 m capsule moving at 6-14 m/s reads
  as a giant next to 10 m buildings. Realistic 1.6 / 4.6 m/s plus head bob and a running
  FOV kick is what makes the world feel human-sized. `FootPlayer` reads *physical keys*,
  so `Input.action_press` will not drive it in tests — use godot-ai `game_manage input_key`.
