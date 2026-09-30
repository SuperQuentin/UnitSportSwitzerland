# Crouch states need a headroom test before standing

- **Crouch states need a headroom test before standing.** `FootPlayer.EndSlide` returns false
  when a standing capsule will not fit (shape query, radius shaved 3 cm), so releasing Ctrl in
  a tunnel keeps you down instead of forcing the body up through the roof — and you cannot
  jump out of a slide you could not stand up in either.
