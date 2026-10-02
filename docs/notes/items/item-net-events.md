# Item net events and placed objects

Two nodes, same path on server and every client (RPCs route by path), made by `ServerWorld` and
`ClientWorld`. Offline (no peer) the client plays the server's part through the same methods.

## Item events — `src/Items/ItemEvents.cs`, `World/ItemEvents`

One-shot, client-authoritative, relayed by the server to the peers that can see the owner
(`InterestService.ServerSees`, like combat tracers). The server draws nothing and drops an event
more than 30 m from the sender's body.

- Send (owner): `ItemEvents.Instance?.Send(ItemEventKind kind, Vector3 position, Vector3 direction, string extra = "")`.
  It runs the handler locally first (the owner sees/hears the 3D effect too), then RPCs the server.
  `ItemEvents.MuzzleOf(FootPlayer p, Vector3 dir, float reach = 0.55f)` = hand + reach along the aim
  (camera fallback in first person).
- Kinds: `ItemEventKind { Shot = 1, PhotoFlash = 2, Hit = 3 }` — append new values, the int goes over the wire.
  `Hit` is not relayed to viewers: the server checks it and sends it to the victim alone (`combat/pvp-weapons`).
  Shot's Extra is the weapon's item id (empty = shotgun).
- Handler: `ItemEvents.Register(kind, (ItemEvents node, ItemEvent e) => ...)` replaces the built-in.
  `ItemEvent(long Peer, Kind, Position, Direction, string Extra, bool Local)`. On remote peers the
  position is recomputed on the owner's interpolated body (`World/Players/<peer>`) when it exists.
  Helpers on the node: `Sound3D`, `LightPulse`, `Glow` (all self-freeing).
- `ItemEvents.Received` (static event): every event run on this peer, after its handler — probes/logs.
- Built-ins: Shot = `SfxSynth.Shotgun` 3D sound + warm OmniLight pulse + additive quad
  (`BirdLife.Fire` sends it; its old non-spatial player is only the fallback when no node exists).
  PhotoFlash = white 0.1 s OmniLight pulse (+ glow and click for others; `ItemController.TakePhoto`
  sends it after the capture, the owner keeps its UI flash). Extra = the photo path.

## Placed objects — `src/Items/PlacedObjects.cs`, `World/Placed`

Server-owned list of `PlacedObject(long Id, PlacedKind Kind, string Owner, double E, double N,
double Altitude, Quaternion Rotation, string Payload)` — LV95 + altitude, so it means the same place
to every peer, whatever their origin (#185); `o.WorldTransform(placed.Origin)` converts.

- Place: `PlacedObjects.Instance.RequestPlace(PlacedKind kind, Transform3D worldAt, string payload, Action<PlacedResult>? done)`.
- Remove: `RequestRemove(long id, Action<PlacedResult>? done)`.
  `PlacedResult(PlacedObject? Object, string? Refused)`, `.Ok`. `done` runs exactly once, also with
  "Disconnected from the server." if the link drops — refund whatever was spent then (the flag does).
- Server checks: kind defined, payload ≤ 512 chars, finite values, requester's body within
  `PlacedObjects.Reach` (10 m) of the spot, ≤ 200 objects per owner; removal only by the owner unless
  the kind is in `PlacedObjects.RemovableByAnyone` (default: `Flag`; `Photo` is owner-only).
  Owner = the chat display name (`NameOfPeer`), "local" offline — a renamed player loses its objects.
- Clients: `Add`/`Remove` broadcasts, `Snapshot` on join (replaces everything shown). `All`,
  `Added`, `Removed` to read/observe.
- Visuals: `PlacedObjects.RegisterFactory(PlacedKind, Func<PlacedObject, Node3D>)` (replaces and
  redraws). The node is positioned for you, named `P<id>`, in group `placed_object`, tagged with meta
  `placed_id`; `PlacedObjects.IdOf(node)` walks up from any collider. Built-ins: `FlagVisual()`
  (planted flag mesh + pole collider), `PhotoVisuals.Placed` (the Polaroid card facing +Z showing its
  print, fetched through `PhotoTransfer`: the `polaroid` note). A dedicated server builds no visuals.
- Persistence: `user://placed/server.json` (server), `user://placed/offline.json` (offline client;
  not loaded with `--connect`). System.Text.Json (culture-invariant), kind saved by name, written
  atomically on every change.
- Flags (`ItemController.PlaceOrPickUpFlag`): the flag leaves the pack at once and comes back on a
  refusal; pickup checks room first and adds the flag on the grant.

## Check

`GODOT=<exe> tools/placedcheck.sh [E,N]`: server (`--generated-world`), A plants (real item path) +
photo + refused far flag, B joins after and must get the snapshot, hear 3 shots + a flash, be refused
removing A's photo (screenshot `test_output/placedcheck_b.png`); server restarted, C finds the flag
and pulls it up. Writes the real `user://placed/server.json` (it ends empty again).
