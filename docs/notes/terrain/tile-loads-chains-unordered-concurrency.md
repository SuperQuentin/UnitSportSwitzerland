# Tile loads are CHAINS, and unordered concurrency starves them

- **Tile loads are CHAINS, and unordered concurrency starves them.** Each tile awaits chunk →
  holes → cover → roads → buildings. Starting all 361 tiles at once means every chain's first
  request goes out before any chain's second, so a streaming client downloads 361 height grids
  and renders none of them — no tile has its cover yet. `MaxConcurrentBuilds` (6) plus
  nearest-first ordering in `EvaluateRings` fixes it: measured 175k prims after 55 s before,
  **4.34 M after 15 s** after. Neither change affects local loading, where the per-frame commit
  budget is the limiter — measured byte-identical at caps of 6 and 24.
