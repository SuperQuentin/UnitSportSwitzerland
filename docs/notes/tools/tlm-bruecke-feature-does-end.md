# A TLM `Bruecke` feature does not end at the abutment

- **A TLM `Bruecke` feature does not end at the abutment.** It ends where its attributes
  change, so a viaduct is several features meeting in mid-air. `RoadExtractor` buffers every
  line before emitting any, so it can index structure endpoints and tell a mid-span join
  (two structure ends at one position) from a real transition to a draped road.
