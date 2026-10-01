# swissTLM3D road attribute value domains (#115)

- `SELECT ... GROUP BY` on `tlm_strassen_strasse`, SWISSTLM3D 2026 (2,096,632 rows). `k_W` =
  "kein Wert", no value.
- **`kreisel`** (roundabout): k_W 1,086,298, Falsch 995,996, **Wahr 14,338** (6m 8,585, 4m 3,656,
  8m 1,595, 10m 329, 3m 106, Autostrasse 30, Autobahn 16, ...). Paths and Verbindung are k_W.
  -> `RoadAttrFlags.Roundabout`.
- **`verkehrsbedeutung`** (traffic importance): k_W 1,968,608, Durchgangsstrasse 59,840,
  Verbindungsstrasse 56,387, Hochleistungsstrasse 11,797 (motorways, ramps, Autostrasse).
  -> high nibble of `Priority` (3 / 2 / 1 / 0).
- **`eigentuemer`** (owner): k_W 1,064,361, ub (other: communal/private) 825,282, Kanton 182,064,
  Bund 24,925. 81k cantonal roads have no `verkehrsbedeutung`, so the owner is a real hint
  for #121 main-road choice. -> `OwnerCanton` / `OwnerFederal`.
- **`stufe`** (grade level): 0 2,029,243, 1 57,631 (mostly Bruecke), -2 8,333 (Unterfuehrung,
  Tunnel), 2 828, -1 470, -3 72, 3 29, 4 18, -4/-5 1 each, k_W 6. -> `Layer`, with the
  bridge/tunnel flags as fallback. RoadGen's graph still layers by the flags (0/+1/-1).
- Also seen, not stored: `befahrbarkeit` (Wahr 939,998, Falsch 172,918, k_W 983,716);
  `richtungsgetrennt` Wahr 39,128 (`Divided`). All 3,663 ramps (Ein/Ausfahrt) are
  `richtungsgetrennt` Wahr with `kreisel` k_W.
