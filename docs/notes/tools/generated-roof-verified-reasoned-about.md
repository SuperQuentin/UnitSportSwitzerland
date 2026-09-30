# A generated roof must be VERIFIED, not reasoned about

- **A generated roof must be VERIFIED, not reasoned about.** Four rounds of fixing individual
  failure modes each roughly halved the damage and none reached zero: the single-ridge model
  bowtied on concave footprints (236/541 buildings), a fan cap emitted backwards triangles on
  concave rings (759/1239), collinear vertices stalled the ear clipper, and — the subtle one — an
  inset wider than the building's half-width turns the offset ring **inside out while every
  vertex is still inside the original**, so a containment check passes it and the roof faces
  down. What actually worked was building the roof into a scratch list and testing the property
  that matters (`FranceBuildings.FacesUp`: every normal above a 75° pitch), falling back to flat
  when it fails. 0 malformed faces of 14,081. Construct-then-verify beats enumerating the ways
  polygon offsetting can go wrong.
