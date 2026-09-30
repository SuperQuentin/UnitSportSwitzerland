# Two players, one container

- **Two players, one container** (verified with two real clients, issue #32). The server grants each
  stack once; a take by a peer whose view is stale is answered with the current contents, not an
  item. After every granted take the server also sends `Taken` to every OTHER peer inside that
  building (`InteriorManager.SpaceOf`), so a panel already open elsewhere drops the stack at once —
  before this it kept offering it until clicked. Check: `GODOT=<exe> tools/lootsynccheck.sh [epoch]
  [E,N]` (server + clients A and B, `LootSyncProbe`, chat lines as the handshake; the two stand side
  by side and each verifies it faces the chosen container, or bodies shove each other onto the
  cupboard next door). The epoch must be fresh — the server remembers what an earlier run took —
  and it writes to the real `user://loot`, so back that up first.
