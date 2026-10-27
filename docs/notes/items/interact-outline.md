# What E acts on is outlined (#1286)

Everything E / the pad's interact / a VR grip acts on shows the border car doors had (`Highlight`,
the stencil silhouette of #401), and the prompt and the border come from one call.

- **`Items.Interaction`** = the prompt's words + an `Items.Outline` target (+ `InBar`: a prompt bar
  row's words, no key tag). Every prompt source of a thing returns one (`…InteractionFor(p)`,
  `FootPlayer.…Interaction()`); the old `PromptFor` strings are wrappers for the probes.
  `InteractOutlineTests` (tier 0) scans `src/`: an interact prompt (`InputHints.Prompt(InteractMount`,
  `Tag(InteractMount)`, a bar row `yield return (InteractMount, …)`) must be made in a method returning
  an `Interaction` (or a words-only helper called only from one, or a bar row taking `x.Text`), or be
  listed there with why (rows for an item or a car door, which have their own pointers; acts with no thing).
- **The rule**: `InteriorManager.Interacting(p)` (`InteriorManager.Interact.cs`), in `FootPlayer.TryInteract`'s
  order, at the floating prompt's cadence (`UpdatePrompt`, 0.15 s). A world item (`Highlight.Pointed`) and a
  vehicle's door (`VehicleReach`) keep their own per-frame pointers: where E takes one first, the rule names
  nothing. Where E takes something before them, `InteractOutline.Outranks` turns them off
  (`ItemController.StepThrow`): a ski lift station or a BR crate before both, a bus button, a crane, a farm
  stand, a reward thing, a place's marker before the vehicle. Change `TryInteract`'s order → change this one.
- **`InteractOutline.Point(Outline)`**, one target, rebuilt on change only (record struct equality):
  - `Outline.Of(node)`: `Highlight.Set` on its meshes. Door leaves (`InteriorNode.Leaf`/`Shutter`,
    `InnerLeaf`; outside a facade pair's `DoorLink.Leaf`), BR crate body, chests and seal props, farm stands
    (`PlacedObjects` `P{id}`), a buyer's office (`FarmBuyerYards.Instance.YardOf`), a ski lift's bottom shed
    (`RopewayLayer.BottomStation`), another player's `Visual`.
  - `Outline.Piece(interiorNode, furniture, half)`: furniture is baked into the interior's one mesh, so
    `InteriorMeshBuilder.Piece(l, i, half)` builds it alone (same rolls: seeded by its X/Z) into a proxy
    child of the `InteriorNode` whose only material is `Highlight.Material` (the mark draws nothing, the
    nudged passes the edge): only the border shows, over the baked piece. `half` -1/+1 keeps the triangles
    left/right of its own X (a cinema booth panel: lights left, screen right, as `CinemaBooths.PanelAt`).
  - `Outline.Box(frame, …)` / `Outline.Around(frame, mesh, at)`: a `BoxMesh` proxy. Lift call plate and
    cabin panel (`InteriorMeshBuilder.LiftCallPlate`/`LiftPanel`, which the baked dressing uses too), a
    facade's shut leaf (from `DoorIndex.Entry`, 6 to 11 cm in front of the wall), MultiMesh markers (trial posts, offering
    boxes, crosses, travel signs: the mesh's AABB at the instance), yard stock (`YardStock` axes), a crane's
    ladder foot (3 m) and remote box, a bus button, the light desk's consoles (`StagePlan.Console`), a
    stand's seat (none is modelled: a box at the hip). A null frame is the scene root (world space).
- Check: `--outlinecheck --systems ui` (`InteractOutlineCheck`): pieces of a planned cinema, flats and shop
  alone and in their footprints, the panel halves, the lift boxes, the outline on / moved / off.
