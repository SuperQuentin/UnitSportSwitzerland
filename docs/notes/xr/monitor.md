# The monitor view while in VR (#186)

- **Where it lives.** `XR/XrMonitor` is a CanvasLayer at layer -100, under the HUD and the menus,
  which the monitor shows as well.
- **Choosing a view.** `GameSettings.VrMonitor` is set in Settings → Video → *Monitor view in VR*.
  F7 cycles it while in VR. `--vrmonitor off|first|eyes|third` sets it for one run.
- **The four views:**
  - **First person** (default): the window's own camera, i.e. the game's camera with the head
    written into it. One picture of what the player sees, head jitter included.
  - **Both eyes**: two SubViewports side by side. Each camera sits where
    `XRInterface.GetTransformForView(i, origin)` puts that eye. Its frustum is rebuilt from
    `GetProjectionForView`: height `2n/Y.y`, offset `(Z.x·n/X.x, Z.y·n/Y.y)`. That reproduces
    the Quest's asymmetric lens frusta. `--xrsim` has no eyes: it uses ±3.2 cm and 90°.
  - **Third person**: a chase camera behind and above the head, along the player's body yaw. A
    mounted player uses the ride's `ChaseDistance` + 1.2 and `ChaseHeight`. The camera eases in,
    and a raycast pulls it in front of slopes and walls.
  - **Off**: no 3D on the monitor; all the GPU goes to the headset.
- **Notices.** `XR/XrNotice` (CanvasLayer 39) shows a one-line notice in the style guide's
  floating-text look: no box, an outline and a shadow, the changed value in amber. It fades out
  after ~1.6 s and shows on both the monitor and the panel. It announces a monitor-view change
  ("Monitor view  Both eyes"), a manual recentre, and with `--xrsim`, "VR  simulated".
- **What each view does to the window.** In Both eyes, Third person and Off, the window's own 3D
  is disabled (`Root.Disable3D`), so nothing renders twice. Each view still costs one render of
  the world (two for Both eyes) on top of the headset's own render.
- **Render layers:**
  - `XrSession.HeadsetOnlyLayer` (bit 14) holds the vignette, the UI panel and the pointer. The
    vignette is a clip-space quad, so it would cover any camera that drew it. The monitor's
    cameras leave this layer out, except with `--xrsim`, where the window is the only view.
  - `XrSession.SpectatorOnlyLayer` (bit 15) holds the VR player's own on-foot body. In VR the
    first-person walker is built anyway, posed every frame (`ApplyFootPose`) and put on this
    layer. Only the third-person camera draws it; the headset camera and the eye views leave it
    out.
  - Mounted visuals are drawn as in flat first person.
- **Doorways.** The eye views draw the portal quads like the window's camera
  (`DoorPortals.QuadLayers[0]`). The chase camera leaves all the portal quads out, because they are
  rendered for the window's viewpoint.
