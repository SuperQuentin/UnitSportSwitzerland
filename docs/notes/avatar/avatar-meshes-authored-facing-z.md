# Avatar meshes are authored facing +Z; a Godot node faces −Z

- **Avatar meshes are authored facing +Z; a Godot node faces −Z.** `MeshScratch.Build` applies the
  half turn on the way out, once, instead of at each of the four places a figure is parented to a
  node. Skipping it does not look like a modelling error — the body travels correctly and only the
  machine is turned around, which from a chase camera reads as *riding in reverse*, and on a GPX
  ghost as running backwards. It survives a preview turntable, where there is no direction of
  travel to contradict it. Related: facing +Z, the rider's right is **−X**, so a chainring at +X
  is on the wrong side of the bike.
