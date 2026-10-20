# Ski and snowboard feel (#933)

Status: done in PR #940 (`feat/933-ski-feel`, stacked on #923).

Playtest feedback: plough, turns too sharp, a stiff figure, no push animation, camera.

1. Turns: lower ski max yaw rate and bank response; check the arc radius at speed stays real.
2. Plough: brake alone opens the skis into a V (drawn), slowing like a real snowplough.
3. Schuss: Shift is a deep tuck: lowest drag, edges off, a stable straight line.
4. Live figure (`SnowRig`): joints computed per frame from a small pose (crouch, angulation,
   plough, stride phase, pole plant, absorb), the figure and its gear rebuilt into one mesh when
   the quantized pose changes; skier and boarder. Replicated as the ride pose (`Vector4`).
5. Camera: first person follows trick spins and flips; the chase camera further back on snow and
   with speed.
6. Snowboard: the same rig, rear-foot push, toe/heel lean, arms balancing.
7. Checks: unit tests, model viewer, ride probe shots of plough/stride/tuck, quick and net tiers.
