# Never fall back to TLM's Z for one vertex of a draped line

- **Never fall back to TLM's Z for one vertex of a draped line.** The two height models
  disagree by metres, so the road grows a spike at exactly that vertex. Interpolate across
  the gap from the neighbours instead (`DrapeHeights`).
