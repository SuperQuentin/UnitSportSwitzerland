# VR roadmap (#186)

**Phase 1 (done):** launch, rig, pad bridge, world UI panel, snap turn, vignette, recentre, seated
cockpit VR, first-person mounts, body skiing, haptics, `--xrsim`.

**Next, in order:**
1. **Avatar head and hands for everyone.** Replicate the head rotation and two hand positions
   (local to the body, 30 Hz, near relay only), plus `IsVr`. Feed them to the `Limb.Solve` arm
   targets so flat players see VR players look, wave and point. Drive the local first-person arms
   the same way.
2. **Hand-held items.** Pose the camera, binoculars and shotgun from the real hands (two-hand aim).
3. **Teleport** on foot, as the comfort alternative to smooth walking.
4. **Room-scale walking** that moves the body. Today the body does not follow the head if you
   step away.
5. Flight triggers (see `controls`), slalom gates via `RaceManager`, ski touring.
6. The asymmetric VR roles: rescue winch, summit spotter, tabletop "Alp spirit".

**Stereo risks to check on a headset:**
- PS1 vertex snap shimmer per eye.
- `DoorPortals` and `CabMirrors` SubViewports in stereo.
- Whether the root 2D canvas leaks into the eyes.
