# GWR: classify on GKLAS, not GKAT

- **GWR: classify on GKLAS, not GKAT.** GKAT only says whether a building is residential
  at all, so using it labels every village house an apartment block. GKLAS 1110/1121 are
  one/two-dwelling houses; 12xx are non-residential.
- **GKLAS 1242 (garages) is its own kind, `BuildingKind.Garage = 10`**, since `.bldg` v2; 1274
  (other minor structures) stays `Annex`. v2 has the v1 layout byte for byte, only the kind
  values differ, so decoders take both; a v1 file simply files its garages as Annex. The client
  cache refetches any cached `.bldg` older than the current version (`NetworkChunkSource`).
  Riddes: 58 of the old 124 Annex are garages. BD TOPO's "Annexe" (France) stays Annex.
