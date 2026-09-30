# AnimatableBody3D is a StaticBody3D

- **`AnimatableBody3D` is a `StaticBody3D`** (Godot 4 class tree). Filtering physics hits with
  `is StaticBody3D` to drop terrain and trunks also drops the traffic (`World/Traffic` units are
  `AnimatableBody3D`): the pilot's sensing saw nothing for a whole run. Test
  `is StaticBody3D and not AnimatableBody3D`.
