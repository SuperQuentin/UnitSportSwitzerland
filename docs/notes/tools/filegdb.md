# Reading Esri FileGDB without GDAL

- **Why** (#537): swissBUILDINGS3D's solids and the ASTRA Veloland / Mountainbikeland networks are
  published only as Esri FileGDB, and they were the last reason this project asked anyone to install
  GDAL. Bundling GDAL is 150-250 MB per platform across three platforms plus macOS codesigning, and
  the format is documented, so `tools/TerrainPreprocessor/FileGdb.cs` reads it directly — the same
  choice `GeoPackageReader` already makes for swissTLM3D.
- **What it reads**: the table catalogue (table 1 names the rest), field descriptors, the
  `.gdbtablx` row index, and rows — null bitmap, varints, strings, ints, doubles, dates, UUIDs.
  `FileGdbGeometry` decodes general multipatches into vertices and parts, and fans them to triangles.
  Not a general implementation: no writing, no `.spx`/`.atx` indexes, no domains. Anything it does
  not understand **throws with its offset** rather than returning a plausible wrong answer.
- **The two things that bite**:
  - **Coordinates are scaled integer deltas**, not floats: `origin + accumulated / scale`, with the
    origin and scale on the geometry column (`FileGdb.GeometryGrid`). Ignore them and the buildings
    land near the South Pole.
  - **X and Y are interleaved pairs**, then Z as a run of its own. Reading X and Y as two separate
    runs decodes the first vertex correctly and every one after it wrongly — which looks like a
    subtle bug rather than an obvious one, and is why the parity check below compares every vertex.
- **The geometry field descriptor's tail does not follow its own flags**: the route networks set
  both the Z and M flags but write only the Z bounds pair. It is determined by trying each
  possibility and keeping the one whose spatial-index grid count is credible; the field block's
  total-length check then proves the choice. That check exists because a descriptor read one byte
  wrong turns every row after it into noise — it is how this was found.
- **Zips**: swisstopo publishes each `.gdb` inside a zip, and a deflated entry has no random access,
  so `OpenZip` unpacks the `.gdbtable`/`.gdbtablx` files (and only those) to a work directory first.
- **Verified against GDAL**, which stays installed on development machines as an oracle only:
  88,676 and 51,745 distinct route keys, a `route_keys.sqlite` identical row for row to the one
  `export_route_keys.py` wrote, and all 3,586 building solids of a sheet compared vertex by vertex
  (worst difference 0.5 mm, which is the dump's rounding). `.bldg` tiles built from the FileGDB hold
  exactly the same buildings as tiles built through GDAL's GeoPackage — same count, same triangles,
  same words — written in a different order, because the GeoPackage returns them in spatial-index
  order and the FileGDB in row order.
- **The feature pass is batched (400 tiles), and the buildings source must be batch-aware** (#570).
  The extractor is opened **once** and run per batch: it holds the GWR cadastre (millions of
  records, seconds to load) and a cache of each sheet's extent, taken from the geometry field
  descriptor, so a batch opens only the sheets it can possibly contain. Built the naive way — a new
  extractor per batch, every sheet rescanned — a 30-tile batch cost the same 11 s as a 210-tile one,
  because the cost tracked the sheet count rather than the tiles; nationwide that is 110 batches
  against 3,230 sheets. Measured after: the first batch pays 5.8 s to open and measure the sheets,
  the rest 0.2-2.4 s each, and the output is byte-identical at the same batch size.
- **`--batch-size` is for checks, not tuning**: a building's terrain re-seat samples only the grids
  of its own batch, so moving the boundaries moves a few buildings at the seams by centimetres.
- **Still Python**: downloading the swissBUILDINGS3D sheets (`swiss_data.py swissbuildings3d`).
  Everything after the download is C#.
