# Beat field: things near a radio move with its music (#734)

- **A radio's own volume** (`RadioLoudness`, pure C#, `RadioLoudnessTests`): 0..1, default 0.6, the
  same for everyone. It sets the speaker's gain (`Db`, 0 dB at the default) and its reach
  (`Radius`, 12 m at 0 to 60 m at 1; ~41 m by default) for both `RadioSpeaker` and the car's
  `WebRadioSpeaker`, and how hard it moves things (`Reach(distance, volume)`: full in the near third,
  fading to 0 at the edge). A world radio's volume is `RadioBody.Volume` on the server-owned `State`
  synchronizer (`RadioManager.SetVolume` -> `RequestVolume`); a carried radio's and a car stereo's
  is the owner's replicated `FootPlayer.RadioVolume`. A throw takes the thrower's along
  (`RadioState.Volume`), a pick-up gives the taker the radio's. The panel's slider turns the radio
  itself; the player's own level is still Settings -> Music (the Music bus).
- **`BeatField`** (client, stepped from `RadioManager._Process`, never headless): gathers the playing
  sources 8 times a second (world radios whose speaker plays, carried radios, car stereos' CDs: up
  to 8) and every frame their position and `RadioGroove` (the CD's analysis on the shared clock), so
  every peer moves the same things on the same beat with nothing on the wire. `At(point, out groove)`
  says how strongly music reaches a point.
- **Drawing only, never physics**: a reaction moves a visual child's transform or a shader vertex,
  never a body or a collider.
  - *Dropped items*: `DroppedItem.PoseFloat` adds a hop and a landing squash to its float pose.
  - *Registered meshes*: `BeatField.Add(mesh, amount, half)` squashes and hops it about its bottom
    (`RadioBody.Bounce`), back at rest when the music stops or leaves; freed nodes drop out by
    themselves. Placed things' meshes (amount 0.7), build pieces' (0.12: a faint pulse).
  - *`IBeatReactive`*: told `OnBeat(reach, groove)` (0 once when it stops): parked `CarRig` /
    `HeavyRig` sink on the kick and rock over two beats (the body node only; wheels and collider stay).
    Registered by `VehicleBody` for its parked visual.
  - *Shaders*: the music the camera hears most goes to the globals `world_music` (xyz, reach) and
    `world_music_beat` (kick, level, bar's one, a two-beat swing), `shaders/common/music.gdshaderinc`
    (`music_reach`). The 3D trees sway (`tree.gdshaderinc`, growing with the height up the tree), the
    occasion props' lights flash with the kicks, by day too (`prop.gdshaderinc`). The game has no
    street lamps.
- **Cost**: fixed arrays, no allocation per frame; registered things farther than 70 m from the camera
  stay at rest; with no music only what was moved is put back.
- **Make something new react**: a mesh, `BeatField.Add(mesh)`; something with its own way to move,
  implement `IBeatReactive` and `BeatField.Add(this, anchor)`; a shader, include `music.gdshaderinc`.
- **Check**: `tools/beatfieldcheck.sh` (full tier, windowed): a radio at full volume moves a dropped
  item's drawing (its body never moves), a mesh beside it and one 20 m off; turned down to nothing
  the far one stops; stopped, all at rest; the shaders get the music. `test_output/beatfield.png`.
