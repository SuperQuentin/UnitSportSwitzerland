# Field state: authority, wire, persistence (#494)

- **The server owns the cells**: `FieldCells` per tile (sparse: only worked cells), saved to
  `user://farm/E_N.json` as `{"Version":2,"Cells":[packed...]}` (`FieldCells.Pack`: cell 16 bits,
  stage 4, crop 8, fertilised 1, Since 32 = environment seconds; a version-1 file held Unix stamps and is not read) through `JsonStore.SaveAsync`
  every 2 s for the tiles that changed (`core/perf-saves-background`). Offline the game is the
  server: the same store, the same file, the same `FarmRules`.
- **RPCs on `World/Farm`** (`FarmField.Net.cs`): `Subscribe(e, n)` / `Unsubscribe(e, n)` (client:
  the tiles it holds), `Work(e, n, tool, seed, seq, cells[])` (client strokes, batched per tile every
  0.2 s, a new batch when the tool or seed changes), `Cells(e, n, month, packed[], snapshot, ack)`
  (server: the snapshot on subscribe, the answer to a Work with every cell sent as stored and
  ack = its seq, other holders' changes with ack 0).
- **Prediction**: `Sweep` applies at once and marks each cell pending with its batch's seq; an
  answer settles a cell unless a newer prediction of it is still out; another peer's change skips a
  pending cell (the own answer that follows is authoritative). A snapshot keeps pending cells.
- **Checks on the server** (loose): the peer's body (`../Players/<peer>`, `FootPlayer.Global`)
  within 90 m of each cell (`WorkReach`: a moving machine, lag), a field there, and the tool can
  work it now (`FarmRules.Work`). Refused cells come back as stored: the prediction is undone.
  **Conflicts: server arrival order.** Item counts (seed used, harvest gained) stay the client's,
  like the pack everywhere else.
- **Late joiners and newly streamed tiles** get the stored cells in the subscribe snapshot; the
  server forgets who held what on disconnect.
- **Versions**: all of #494 is `Handshake.Protocol` 26 (fields, farm state, machines, selling), so
  two versions never meet. Within it the fields degrade on their own anyway: a source without the
  layer answers `AssetKind.Fields` as missing and the fields stay natural.
- **Check**: `tools/farmnetcheck.sh` (tier 2): A ploughs a strip (machine stroke) and tills a cell
  by hand, every cell answered; B joins after and gets the strip from the snapshot, sees A's second
  strip live, sees the cell A then tills and sows with a hoe and a seed bag from its pack (hotbar
  slot + `use_item`, on a lent empty pack) sown with wheat, is refused ploughing the potatoes from 200 m (back to growing); the server's farm file
  holds the cells.
