# Licenses page

- **Settings > About > Licenses** (`Core/Licenses.cs` list, drawn by `SettingsMenu.ShowLicenses`,
  `--licenses` opens it for a screenshot). One entry per data source, engine or bundled component:
  name, attribution, what it is used for, licence and a link. To add one, append to
  `Licenses.All`. Below the list the page prints Godot's own `Engine.GetLicenseText()` and the
  component/licence pairs from `Engine.GetCopyrightInfo()`, which is what Godot asks a game to show.
- **Checked 2026-09-30 against the owners' pages**: swisstopo OGD terms ("A reference to the
  source is mandatory"; "©swisstopo" is one accepted form); GWR on opendata.swiss is `terms_open`
  (source recommended, credited anyway); ASTRA Veloland/Mountainbikeland metadata requires
  "Bundesamt für Strassen, Kanton, Stiftung SchweizMobil" (`terms_by`); IGN BD TOPO is Licence
  Ouverte 2.0; OSM is ODbL (`docs/notes/tools/osm-odbl-licence.md`); Godot, GodotSharp and the SDK
  are MIT; `addons/godot_ai` is MIT (it ships: the plugin's game helper is an autoload); `icon.svg`
  is the Godot logo, CC BY 4.0, Andrea Calabró. yt-dlp (Unlicense) and FFmpeg (LGPL/GPL) are not
  bundled; a server runs them for radio CDs if installed.
- **Not settled**: Licence Ouverte also asks for the date of the data's last update, which the
  French import does not record (it reads the live WFS); BFS's own terms page is JavaScript-only
  and could not be read. Build-only tools (Spectre.Console MIT, Microsoft.Data.Sqlite MIT,
  SQLitePCLRaw Apache-2.0, GDAL) do not ship in the game and are not listed.
- LinkButton only takes focus for screen readers by default in 4.7 ("can grab focus only when
  screen reader is active"), so `PlayerInput.FocusFirst` failed on the page until the links got
  `FocusMode = All`.
