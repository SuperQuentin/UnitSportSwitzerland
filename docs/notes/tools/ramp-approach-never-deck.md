# Ramp the APPROACH, never the deck

- **Ramp the APPROACH, never the deck.** Two wrong versions came before the right one.
  (1) Interpolating each deck point toward the ground beneath it pulls the middle of a short
  span down to the river bed — measured 904 m -> 886 m -> 904 m across a 12 m bridge, the
  V-notch in the middle of a viaduct. (2) Gating that blend on a small height mismatch keeps
  decks flat but leaves a hard step wherever the ground genuinely is metres below the
  abutment. The deck is right and the drape is wrong: swissALTI3D does not model the
  embankment that climbs to a bridge. So a *draped* line whose true end coincides with a
  structure endpoint takes `delta = structureZ - draped[end]` and fades it out inland
  (`ApproachDelta` + `Falloff`); structures themselves get no blend at all. Region measured:
  joins stepping >0.5 m went 814 -> 33, deck spans over 50% grade 5,770 -> 471.
