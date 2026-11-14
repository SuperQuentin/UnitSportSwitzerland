# NetLink answers once a frame, from the tree's API (#1910)

`Net/NetLink` (`Ready`, `Online`, `Dropped`, `IsServer`, `UniqueId`) is asked hundreds of times a frame by per-frame
code (`FootPlayer` above all). It used to read `node.Multiplayer` on every call, and that makes the engine build the
node's path to look for a subtree's own multiplayer API (the game never sets one: every node shares the tree's). In
`cinemanetcheck` under earth that was ~8 % of a client's main thread.

- **Now**: on the main thread the link (peer, offline, status, server flag, unique id) is read once a frame from
  `GetTree().GetMultiplayer()`, keyed on a managed frame count (`NetLink.Tick`, the tree's `process_frame`), so a cached
  answer costs no engine call at all (each interop call showed 1-4 % in the traces).
- **Fresh reads**: `NetLink.Invalidate()` after every place the game sets its peer (`ClientWorld`, `GameShell`,
  `NetworkManager`), and on the API's own signals (connected, connection failed, server gone, peer joined or left: the
  engine polls before `process_frame`, so those handlers must not read the frame before's state). A status change with
  no signal (a peer closed by our own code) shows the next frame.
- Off the main thread, and for a node outside the tree before the frame's read, each call reads the link itself.
- `ClientWorld.LocalPlayer` keys on `NetLink.Tick` too: answered once a frame, the node kept until freed, moved or
  renumbered (a path made from a string per call was ~6 %).
- Never read `node.Multiplayer` in a per-frame path: ask `NetLink`, or `GetTree().GetMultiplayer()`.
