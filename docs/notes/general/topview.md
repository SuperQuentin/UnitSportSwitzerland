# Top view: a recorded playtest from above (#844)

One model, two outputs (`src/Playtest/TopView/`): `TopViewScene.Build` turns a window of trace
frames into shapes in LV95 metres (no engine, Tier 0); the live overlay draws them with
`Control._Draw`, the offline command as an SVG rasterised to PNG.

## What it draws

- A track per thing over the window, a dot every whole second (so speed reads as spacing).
- Vehicles and objects as their measured box, the thick edge at the front, labelled; a player
  as an arrow, over the box of what it drives (the ride's footprint is in the trace).
- A velocity line per moving thing: where it would be a second later.
- Notes as red pins; birds as faint tracks only, never fitted to.
- With a server trace: the server's tracks dashed red, its boxes and arrows outlined, and a red
  `Δ x.x m` line from where this client drew each thing to where the server had it.
- Colours: you dark blue, other players blue, vehicles orange, traffic grey, items green, others purple.

## Offline

```
tools/playtest.sh topview [dir] --from 40 --to 60 [--at 52] [--around E,N --radius 30] [--out test_output/x.png] [--size 900]
tools/playtest.sh topview [dir] --from 40 --to 60 --html      # a page with a time slider (arrow keys step)
```

(`--topview <dir> ...` on the game's command line, headless, no world.) Times are the report's:
seconds since the session began. Default: the session's last 30 s, the square fitted to the
tracks. `--server <dir>` names the server's bundle; `<dir>/server` is used when it exists. Claude
can `Read` the PNG. ThorVG draws no SVG `<text>` headless, so the PNG's labels are a 5×7 pixel
font drawn as paths (`PixelFont`; upper case, accents dropped); the HTML keeps real text.

## Live

Shift+F12 shows it bottom left, centred on you, the last 10 s, 150 m across; Shift + wheel zooms
(25 to 400 m). Ctrl+F12 stops it on the moment shown, Ctrl + wheel scrubs the last minute, Ctrl+F12
again is live. The game never pauses. A pad and VR toggle it from the playtest panel (Top view);
the zoom and the scrub are keyboard only. MCP `topview(from, to, at, e, n, radius)` returns the
same picture while the game runs.
