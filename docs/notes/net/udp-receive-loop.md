# UDP receive loop: one helper (`Udp.ReceiveLoop`)

## Rule
- A background UDP reader MUST go through `Udp.ReceiveLoop(udp, token, (bytes, from) => ...)`
  (`src/Net/Udp.cs`), started with `Task.Run`. Never hand-write another `while (!token...) { await udp.ReceiveAsync }` loop.
- The handler runs on a thread-pool thread: no Godot node access in it (hand results over with a
  `ConcurrentQueue` / `CallDeferred`, as `LanDiscovery` and `ServerQuery` do).
- Reply from inside the handler with the synchronous `udp.Send(...)`; do not `await` in it.

## Why
#221 investigation 3/3, cluster #16: the same 12-line loop lived in `LanDiscovery`, `ServerQuery` and
`QueryResponder`. Pure consolidation (no runtime cost change; one closure per loop, not per packet). PR #268.

## Same logic, preserved
- Stops on cancellation or `ObjectDisposedException` (socket closed by `Stop`/`Dispose`).
- `SocketException` on receive is skipped, not fatal: Windows reports an ICMP port unreachable (a
  queried server that is down) on the NEXT receive. Removing that `continue` kills the LAN list after
  the first dead saved server.
- Any exception from the handler drops that one packet only (malformed packet, asker gone).
- `QueryResponder` replies with `udp.Send` instead of `await SendAsync`: same datagram, a UDP send
  does not block; a send error is swallowed by the loop as before.

## Migrating old code / open branches
- `grep -rn "ReceiveAsync(token)" src/` — any hit outside `Udp.cs` is an old loop: move its body after
  the receive into a lambda `(p, from) => ...`, `continue` becomes `return`, `got.Buffer` → `p`,
  `got.RemoteEndPoint` → `from`.
- Removed methods: `LanDiscovery.ReceiveLoop`, `ServerQuery.ReceiveLoop` (call sites now call
  `Udp.ReceiveLoop` directly); `QueryResponder.Loop` is kept as a thin wrapper. A branch editing those
  old loop bodies conflicts there: re-apply the edit inside the lambda.
- A branch that changes `QueryResponder`'s status snapshot (`Refresh`, `_snapshot`) does not touch
  the loop region; the lambda still reads `Volatile.Read(ref _snapshot)`.

## How to check
Headless server, then `<godot> --headless --path . -- --discovercheck 8`: must print a
`[discover] udp ... players= ping=` line for the server and `[discover] RESULT: ok`.
