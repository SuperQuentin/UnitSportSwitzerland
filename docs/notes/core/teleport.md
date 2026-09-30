# Teleport

- **Teleport** (`Core/Teleporter`): resolves *what to move* at the moment of the jump, not at
  construction. Flying camera gets ground + 220 m, a `CharacterBody3D` gets ground + 2 m and
  has its velocity zeroed and its placement pass re-armed.
