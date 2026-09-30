# Mantle

- **Mantle** (on foot): pushing into a wall whose top is 0.45–2.1 m above the feet, with open air
  over it and standing room on it, pulls you up (automatic in the air, needs Jump on the ground so
  walking into garden walls does not vault them). Jump + mantle therefore reaches ~3 m. Moved
  directly, not through MoveAndSlide, which exists to stop exactly this contact. Ground coyote
  time 0.12 s. Check: `<godot> --path . -- --mantlecheck` (1.4 m and 2.8 m must climb, 3.6 m not).
