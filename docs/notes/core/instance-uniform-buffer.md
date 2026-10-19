# The instance uniform buffer (`rendering/limits/global_shader_variables/buffer_size`)

- Every node drawn with a shader that has an `instance uniform` takes a block of Godot's global shader
  buffer: 16 vec4 slots, whatever it declares. The default 65 536 is about 4 096 such nodes.
- Ours: every terrain tile (`photo_slot`, `shaders/body/terrain.gdshaderinc`), every figure (the face's
  three, `face.gdshaderinc`), every interior (`disco`, `disco_center`). Flying in spectator with a
  150 km horizon loads enough tiles to pass 4 096; every node past it logs "Too many instances using
  shader instance variables. Increase buffer size in Project Settings." (3 104 in one session, Oct
  2026), and its instance values do not reach the GPU.
- Set to 262 144 (about 16 000 nodes, 4 MB of GPU memory) in `project.godot`.
- Also: a pose or a mesh swapped in on a figure's MeshInstance3D is a new base: keep one mesh and
  rewrite its surfaces (`ConcertPerformers.Step`), as `FootPlayer` builds `into:` its own mesh.
