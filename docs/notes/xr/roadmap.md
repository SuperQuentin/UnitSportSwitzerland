# VR roadmap (#186)

**Phase 1 (done):** launch, rig, pad bridge, world UI panel, snap turn, vignette, recentre, seated
cockpit VR, first-person mounts, body skiing, haptics, `--xrsim`.

**Next, in order** (the per-action target design is `vr-action-map`):
1. **Avatar head and hands for everyone.** Hands: done on foot (#439, `avatar/item-arm-poses`); the
   head's turn and the arms when mounted are still to do. Replicate the head rotation and two hand positions
   (local to the body, 30 Hz, near relay only), plus `IsVr`. Feed them to the `Limb.Solve` arm
   targets so flat players see VR players look, wave and point. Drive the local first-person arms
   the same way.
2. **Hand-held items.** (Wheel and doors by hand: done, #243.) Pose the camera, binoculars and shotgun from the real hands (two-hand aim).
3. ~~**Teleport** on foot~~: done, with the comfort settings (#439, `rig`).
4. **Room-scale walking** that moves the body. Today the body does not follow the head if you
   step away.
5. Flight triggers (see `controls`), slalom gates via `RaceManager`, ski touring.
5b. Done in #439: teleport and comfort, hands on the avatar, wall mirrors, hand-held map, climbing, watch.
6. The asymmetric VR roles: rescue winch, summit spotter, tabletop "Alp spirit".

**Stereo risks to check on a headset:**
- ~~PS1 vertex snap shimmer per eye~~: off in VR (#244, `air-link`).
- ~~`DoorPortals` in stereo~~: one picture per eye (#244, `docs/notes/terrain/door-portals.md`).
- `CabMirrors` in stereo (a mono picture on a UV-mapped mirror, expected fine).
- Whether the root 2D canvas leaks into the eyes.
