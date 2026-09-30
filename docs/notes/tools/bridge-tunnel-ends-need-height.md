# Bridge/tunnel ends need a height blend

- **Bridge/tunnel ends need a height blend.** Structures keep their surveyed Z while the
  approach is draped, so the join steps unless the last ~9 m is smoothstepped between the
  two. Blend only at *true* polyline ends (`Piece.AtLineStart/AtLineEnd`) — blending at a
  tile-clip boundary would dip the deck mid-span.
