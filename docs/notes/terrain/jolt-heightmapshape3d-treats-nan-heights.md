# Jolt HeightMapShape3D treats NaN heights as holes

- **Jolt HeightMapShape3D treats NaN heights as holes** (Godot's default physics engine
  here). One NaN vertex removes the four quads touching it, so collision opens slightly
  wider than the visual mesh — which is the safe direction.
