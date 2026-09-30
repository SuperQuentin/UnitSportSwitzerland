# Window rows crop unless storeys divide the wall exactly

- **Window rows crop unless storeys divide the wall exactly.** Pick the storey height so
  `usable / count` is whole, and pass the count via UV2 so the shader stops below the wall
  plate — otherwise the eave slices the top row.
