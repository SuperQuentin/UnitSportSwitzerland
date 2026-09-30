using System.Runtime.InteropServices;
using System.Text;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.MapSetup;

/// <summary>
/// The full-screen map of Switzerland the zone is picked on. Drawn with raw 24-bit ANSI
/// rather than Spectre widgets: a map is thousands of individually coloured cells per frame,
/// and one pre-built string written once per keypress is what keeps it instant at zoom 1.
/// Each character cell is two map pixels (the upper half block ▀, foreground above,
/// background below), so the country fits a normal terminal at 4 km per pixel.
/// </summary>
public sealed class MapView
{
    private const int PanelW = 36;
    private static readonly int[] Zooms = [8, 4, 2, 1];

    private readonly CountryData _country;
    private readonly LocalState _local;
    private readonly Selection _sel;
    private readonly Func<Selection, IReadOnlyList<(string Label, string Value)>> _summary;

    // cursor = top-left tile of the pixel block under it, aligned to the zoom grid
    private int _cE = 2600, _cN = 1199;
    private int _zoomIndex = 1;
    private int Zoom => Zooms[_zoomIndex];
    // view origin: left tile column and top tile row (the highest N on screen), zoom-aligned
    private int _viewLeft, _viewTop;
    private int _termW, _termH;

    private TileId? _anchor;
    private bool _anchorRemove;
    private Brush _brush = Brush.Off;
    private int _brushKm;
    private bool _help;
    private string? _message;

    private Mode _mode = Mode.Map;
    private string _input = "";
    private int _pick;
    private List<Town> _matches = new();
    private Town? _pickedTown;
    private (string Question, Action Yes)? _confirm;
    private bool? _result;

    private int _summaryVersion = -1;
    private IReadOnlyList<(string Label, string Value)> _summaryLines = [];

    private enum Brush { Off, Paint, Erase }
    private enum Mode { Map, Search, Radius, Canton }

    public MapView(CountryData country, LocalState local, Selection selection,
        Func<Selection, IReadOnlyList<(string Label, string Value)>> summary)
    {
        _country = country;
        _local = local;
        _sel = selection;
        _summary = summary;
    }

    /// <summary>True: continue with the selection (Enter). False: the user quit.</summary>
    public bool Run()
    {
        EnableVirtualTerminal();
        var oldEncoding = Console.OutputEncoding;
        Console.OutputEncoding = new UTF8Encoding(false);
        bool oldCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        Write("\x1b[?1049h\x1b[?25l");
        try
        {
            _termW = Console.WindowWidth;
            _termH = Console.WindowHeight;
            InitialView();
            Render();
            while (_result == null)
            {
                if (Console.KeyAvailable)
                {
                    // drain everything queued (a held arrow key) before the next redraw
                    while (Console.KeyAvailable && _result == null) HandleKey(Console.ReadKey(true));
                    Render();
                }
                else if (Console.WindowWidth != _termW || Console.WindowHeight != _termH)
                {
                    _termW = Console.WindowWidth;
                    _termH = Console.WindowHeight;
                    CentreOnCursor();
                    Render();
                }
                else Thread.Sleep(25);
            }
            return _result.Value;
        }
        finally
        {
            Write("\x1b[0m\x1b[?25h\x1b[?1049l");
            Console.TreatControlCAsInput = oldCtrlC;
            Console.OutputEncoding = oldEncoding;
        }
    }

    // ---- geometry ----------------------------------------------------------------------

    private int MapW => Math.Max(10, _termW - PanelW - 1);
    private int MapRows => Math.Max(4, _termH - 2);
    private int MapPixH => MapRows * 2;

    private int AlignE(int e) => CountryData.MinE + FloorDiv(e - CountryData.MinE, Zoom) * Zoom;
    private int AlignN(int n) => CountryData.MaxN - FloorDiv(CountryData.MaxN - n, Zoom) * Zoom;
    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    private void InitialView()
    {
        if (_sel.Centre() is { } c)
        {
            _cE = c.E;
            _cN = c.N;
            _zoomIndex = 2;
        }
        else
        {
            // the most detail that still fits the whole country (350 x 223 km)
            _zoomIndex = 0;
            for (int i = Zooms.Length - 1; i >= 0; i--)
                if (MapW * Zooms[i] >= 350 && MapPixH * Zooms[i] >= 224) { _zoomIndex = i; break; }
        }
        _cE = AlignE(_cE);
        _cN = AlignN(_cN);
        if (_sel.Count == 0)
        {
            // frame the country, not the cursor: Bern is off-centre in it
            _viewLeft = AlignE(2659 - MapW * Zoom / 2);
            _viewTop = AlignN(1185 + MapPixH * Zoom / 2);
        }
        else CentreOnCursor();
    }

    private void CentreOnCursor()
    {
        _viewLeft = AlignE(_cE - MapW / 2 * Zoom);
        _viewTop = AlignN(_cN + MapPixH / 2 * Zoom);
    }

    private (int X, int Y) CursorPixel() => ((_cE - _viewLeft) / Zoom, (_viewTop - _cN) / Zoom);

    private void KeepCursorVisible()
    {
        var (x, y) = CursorPixel();
        int m = 3;
        if (x < m || x >= MapW - m || y < m || y >= MapPixH - m) CentreOnCursor();
    }

    /// <summary>The tiles of the pixel block whose top-left tile is (e, n).</summary>
    private IEnumerable<TileId> Block(int e, int n)
    {
        for (int dn = 0; dn < Zoom; dn++)
            for (int de = 0; de < Zoom; de++)
                yield return new TileId(e + de, n - dn);
    }

    // ---- input -------------------------------------------------------------------------

    private void HandleKey(ConsoleKeyInfo k)
    {
        _message = null;
        if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            _result = false;
            return;
        }
        if (_confirm is { } q)
        {
            _confirm = null;
            if (k.Key == ConsoleKey.Y) q.Yes();
            return;
        }
        switch (_mode)
        {
            case Mode.Search: SearchKey(k); return;
            case Mode.Radius: RadiusKey(k); return;
            case Mode.Canton: CantonKey(k); return;
        }

        int step = k.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 10 : 1;
        switch (k.Key)
        {
            case ConsoleKey.LeftArrow: Move(-step, 0); break;
            case ConsoleKey.RightArrow: Move(step, 0); break;
            case ConsoleKey.UpArrow: Move(0, step); break;
            case ConsoleKey.DownArrow: Move(0, -step); break;
            case ConsoleKey.Home: Move(-10, 0); break;
            case ConsoleKey.End: Move(10, 0); break;
            case ConsoleKey.PageUp: Move(0, 10); break;
            case ConsoleKey.PageDown: Move(0, -10); break;

            case ConsoleKey.OemPlus or ConsoleKey.Add: SetZoom(_zoomIndex + 1); break;
            case ConsoleKey.OemMinus or ConsoleKey.Subtract: SetZoom(_zoomIndex - 1); break;

            case ConsoleKey.R: Rect(remove: false); break;
            case ConsoleKey.E: Rect(remove: true); break;
            case ConsoleKey.Spacebar: ToggleBlock(); break;
            case ConsoleKey.B:
                _brush = _brush == Brush.Paint ? Brush.Off : Brush.Paint;
                if (_brush != Brush.Off) ApplyBrush();
                break;
            case ConsoleKey.N:
                _brush = _brush == Brush.Erase ? Brush.Off : Brush.Erase;
                if (_brush != Brush.Off) ApplyBrush();
                break;
            case ConsoleKey.Oem4: _brushKm = Math.Max(0, _brushKm - 1); break;   // [
            case ConsoleKey.Oem6: _brushKm = Math.Min(10, _brushKm + 1); break;  // ]

            case ConsoleKey.F or ConsoleKey.Oem2 or ConsoleKey.Divide:
                _mode = Mode.Search; _input = ""; _pick = 0; _matches = new();
                break;
            case ConsoleKey.C:
                _mode = Mode.Canton; _pick = 0;
                break;
            case ConsoleKey.X:
                if (_sel.Count > 0) _confirm = ($"Clear all {_sel.Count} selected tiles? (y/n)", _sel.Clear);
                break;
            case ConsoleKey.H: _help = !_help; break;

            case ConsoleKey.Enter:
                if (_sel.Count == 0) _message = "Select at least one tile first (R, Space, B, F or C).";
                else _result = true;
                break;
            case ConsoleKey.Escape when _anchor != null:
                _anchor = null;
                break;
            case ConsoleKey.Escape or ConsoleKey.Q:
                _confirm = ("Quit without downloading anything? (y/n)", () => _result = false);
                break;
            default:
                // '?' has no ConsoleKey of its own on every layout
                if (k.KeyChar == '?') _help = !_help;
                else if (k.KeyChar == '+') SetZoom(_zoomIndex + 1);
                else if (k.KeyChar == '-') SetZoom(_zoomIndex - 1);
                else if (k.KeyChar == '[') _brushKm = Math.Max(0, _brushKm - 1);
                else if (k.KeyChar == ']') _brushKm = Math.Min(10, _brushKm + 1);
                else if (k.KeyChar == '/') { _mode = Mode.Search; _input = ""; _pick = 0; _matches = new(); }
                break;
        }
    }

    private void Move(int dx, int dy)
    {
        _cE = Math.Clamp(_cE + dx * Zoom, CountryData.MinE, CountryData.MaxE);
        _cN = Math.Clamp(_cN + dy * Zoom, CountryData.MinN, CountryData.MaxN);
        _cE = AlignE(_cE);
        _cN = AlignN(_cN);
        KeepCursorVisible();
        if (_brush != Brush.Off) ApplyBrush();
    }

    private void SetZoom(int index)
    {
        index = Math.Clamp(index, 0, Zooms.Length - 1);
        if (index == _zoomIndex) return;
        // keep the same place under the cursor: its centre, re-snapped to the new block grid
        int ce = _cE + Zoom / 2, cn = _cN - Zoom / 2;
        _zoomIndex = index;
        _cE = AlignE(ce);
        _cN = AlignN(cn);
        CentreOnCursor();
    }

    private void Rect(bool remove)
    {
        if (_anchor is not { } a || _anchorRemove != remove)
        {
            _anchor = new TileId(_cE, _cN);
            _anchorRemove = remove;
            _message = remove ? "Erase: move to the opposite corner, E again to erase." : "Move to the opposite corner, R again to add.";
            return;
        }
        // both corners are pixel blocks: the rectangle spans them completely
        int minE = Math.Min(a.E, _cE), maxE = Math.Max(a.E, _cE) + Zoom - 1;
        int maxN = Math.Max(a.N, _cN), minN = Math.Min(a.N, _cN) - Zoom + 1;
        int before = _sel.Count;
        _sel.AddRect(new TileId(minE, minN), new TileId(maxE, maxN), remove);
        _message = remove ? $"Removed {before - _sel.Count} tiles." : $"Added {_sel.Count - before} tiles.";
        _anchor = null;
    }

    private void ToggleBlock()
    {
        var tiles = Block(_cE, _cN).Where(_country.Covered).ToList();
        if (tiles.Count == 0)
        {
            _message = "Nothing to download there (outside swissALTI3D's coverage).";
            return;
        }
        bool add = tiles.Any(t => !_sel.Contains(t));
        foreach (var t in tiles)
            if (add) _sel.Add(t); else _sel.Remove(t);
    }

    private void ApplyBrush()
    {
        bool remove = _brush == Brush.Erase;
        if (_brushKm == 0)
        {
            foreach (var t in Block(_cE, _cN))
                if (remove) _sel.Remove(t); else _sel.Add(t);
        }
        else
            _sel.AddCircle(_cE * 1000.0 + Zoom * 500.0, (_cN + 1) * 1000.0 - Zoom * 500.0, _brushKm, remove);
    }

    private void SearchKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape: _mode = Mode.Map; return;
            case ConsoleKey.UpArrow: _pick = Math.Max(0, _pick - 1); return;
            case ConsoleKey.DownArrow: _pick = Math.Min(Math.Max(0, _matches.Count - 1), _pick + 1); return;
            case ConsoleKey.Enter:
                if (_matches.Count == 0) return;
                _pickedTown = _matches[_pick];
                _mode = Mode.Radius;
                _input = _pickedTown.Kind == PlaceKind.Town ? "5" : "3";
                return;
            case ConsoleKey.Backspace:
                if (_input.Length > 0) _input = _input[..^1];
                break;
            default:
                if (!char.IsControl(k.KeyChar)) _input += k.KeyChar;
                break;
        }
        _matches = _country.Search(_input, 10);
        _pick = 0;
    }

    private void RadiusKey(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape: _mode = Mode.Map; return;
            case ConsoleKey.Backspace:
                if (_input.Length > 0) _input = _input[..^1];
                return;
            case ConsoleKey.Enter:
                var town = _pickedTown!;
                double km = double.TryParse(_input, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 5;
                km = Math.Clamp(km, 0, 60);
                int before = _sel.Count;
                _sel.AddCircle(town.E, town.N, km);
                _cE = AlignE(town.Tile.E);
                _cN = AlignN(town.Tile.N);
                CentreOnCursor();
                _message = $"{town.Name}: added {_sel.Count - before} tiles within {km:0.#} km.";
                _mode = Mode.Map;
                return;
            default:
                if (char.IsDigit(k.KeyChar) || (k.KeyChar == '.' && !_input.Contains('.'))) _input += k.KeyChar;
                return;
        }
    }

    private void CantonKey(ConsoleKeyInfo k)
    {
        var list = _country.Cantons;
        if (list.Count == 0)
        {
            _mode = Mode.Map;
            _message = "This country file has no cantons (re-run --bake).";
            return;
        }
        switch (k.Key)
        {
            case ConsoleKey.Escape: _mode = Mode.Map; return;
            case ConsoleKey.UpArrow: _pick = (_pick + list.Count - 1) % list.Count; return;
            case ConsoleKey.DownArrow: _pick = (_pick + 1) % list.Count; return;
            case ConsoleKey.Enter:
                var canton = list[_pick];
                int before = _sel.Count;
                _sel.AddCanton(canton);
                var tiles = _country.TilesInCanton(canton.Id).ToList();
                if (tiles.Count > 0)
                {
                    _cE = AlignE((int)tiles.Average(t => t.E));
                    _cN = AlignN((int)tiles.Average(t => t.N));
                    CentreOnCursor();
                }
                _message = $"{canton.Name}: added {_sel.Count - before} tiles.";
                _mode = Mode.Map;
                return;
            default:
                // typing a letter jumps to the next canton whose code or name starts with it
                if (char.IsLetter(k.KeyChar))
                {
                    char c = char.ToUpperInvariant(k.KeyChar);
                    for (int i = 1; i <= list.Count; i++)
                    {
                        int j = (_pick + i) % list.Count;
                        if (char.ToUpperInvariant(list[j].Code[0]) == c || char.ToUpperInvariant(list[j].Name[0]) == c)
                        {
                            _pick = j;
                            break;
                        }
                    }
                }
                return;
        }
    }

    // ---- rendering ---------------------------------------------------------------------

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Mix(Rgb a, Rgb b, float t) => new(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        public Rgb Scale(float f) => new(
            (byte)Math.Clamp(R * f, 0, 255), (byte)Math.Clamp(G * f, 0, 255), (byte)Math.Clamp(B * f, 0, 255));
    }

    private static readonly Rgb Void = new(14, 17, 24);
    private static readonly Rgb Downloaded = new(60, 110, 230);
    private static readonly Rgb BuiltColor = new(40, 200, 200);
    private static readonly Rgb Selected = new(255, 190, 30);
    private static readonly Rgb Pending = new(255, 235, 140);
    private static readonly Rgb Cursor = new(255, 40, 220);
    private static readonly Rgb PanelBg = new(24, 26, 32);
    private static readonly Rgb Text = new(220, 222, 228);
    private static readonly Rgb Dim = new(140, 145, 155);

    private static readonly (float M, Rgb C)[] Hypso =
    [
        (0, new(96, 128, 96)), (300, new(92, 142, 78)), (700, new(122, 160, 88)),
        (1200, new(152, 156, 98)), (1800, new(146, 124, 96)), (2400, new(150, 146, 140)),
        (3000, new(208, 208, 214)), (4000, new(250, 250, 255)),
    ];

    private static Rgb Elevation(int m)
    {
        if (m <= 0) return new(100, 118, 104);
        for (int i = 1; i < Hypso.Length; i++)
            if (m <= Hypso[i].M)
                return Rgb.Mix(Hypso[i - 1].C, Hypso[i].C, (m - Hypso[i - 1].M) / (Hypso[i].M - Hypso[i - 1].M));
        return Hypso[^1].C;
    }

    private struct Pixel
    {
        public int Covered, Sel, Built, Down, Pend, MaxElev;
        public byte Canton;
    }

    private void Render() => Write(BuildFrame());

    /// <summary>
    /// One frame of the map at a given terminal size, after replaying <paramref name="keys"/> as if
    /// typed: the ANSI text that would be written. How the screen is checked without a terminal
    /// (<c>--snapshot</c> turns it into HTML).
    /// </summary>
    public string Snapshot(int width, int height, IEnumerable<ConsoleKeyInfo> keys)
    {
        _termW = width;
        _termH = height;
        InitialView();
        foreach (var k in keys) HandleKey(k);
        return BuildFrame();
    }

    private string BuildFrame()
    {
        if (_termW < 80 || _termH < 20)
            return "\x1b[H\x1b[0m\x1b[2J" + $"Enlarge the terminal to at least 80x20 (now {_termW}x{_termH}).";
        if (_sel.Version != _summaryVersion)
        {
            _summaryLines = _summary(_sel);
            _summaryVersion = _sel.Version;
        }

        int w = MapW, ph = MapPixH, z = Zoom;
        var px = new Pixel[w, ph];
        (int minE, int maxE, int minN, int maxN)? pend = null;
        if (_anchor is { } a)
            pend = (Math.Min(a.E, _cE), Math.Max(a.E, _cE) + z - 1, Math.Min(a.N, _cN) - z + 1, Math.Max(a.N, _cN));

        for (int y = 0; y < ph; y++)
            for (int x = 0; x < w; x++)
            {
                int e0 = _viewLeft + x * z, n0 = _viewTop - y * z;
                ref var p = ref px[x, y];
                for (int dn = 0; dn < z; dn++)
                    for (int de = 0; de < z; de++)
                    {
                        var t = new TileId(e0 + de, n0 - dn);
                        if (!_country.Covered(t)) continue;
                        p.Covered++;
                        if (_sel.Contains(t)) p.Sel++;
                        if (_local.Built.Contains(t)) p.Built++;
                        else if (_local.Downloaded.Contains(t)) p.Down++;
                        if (pend is { } r && t.E >= r.minE && t.E <= r.maxE && t.N >= r.minN && t.N <= r.maxN) p.Pend++;
                        p.MaxElev = Math.Max(p.MaxElev, _country.MaxElevation(t));
                    }
                if (p.Covered > 0)
                    p.Canton = _country.CantonAt(new TileId(e0 + z / 2, n0 - z / 2))?.Id
                               ?? _country.CantonAt(new TileId(e0, n0))?.Id ?? 0;
            }

        var (cx, cy) = CursorPixel();
        var colors = new Rgb[w, ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < w; x++)
            {
                var p = px[x, y];
                Rgb c;
                if (p.Covered == 0) c = Void;
                else
                {
                    // light from the north-west: a slope rising toward the viewer's light is brighter
                    int nw = x > 0 && y > 0 && px[x - 1, y - 1].Covered > 0 ? px[x - 1, y - 1].MaxElev : p.MaxElev;
                    float shade = Math.Clamp(1f + (p.MaxElev - nw) / (500f * MathF.Sqrt(z)) * 0.5f, 0.66f, 1.34f);
                    c = Elevation(p.MaxElev).Scale(shade);
                    // light enough that the relief still reads through: the tint is status, not terrain
                    if (p.Built * 2 >= p.Covered) c = Rgb.Mix(c, BuiltColor, 0.28f);
                    else if (p.Down * 2 >= p.Covered) c = Rgb.Mix(c, Downloaded, 0.35f);
                    if (p.Sel > 0) c = Rgb.Mix(c, Selected, p.Sel * 2 >= p.Covered ? 0.78f : 0.45f);
                    if (p.Pend > 0) c = Rgb.Mix(c, Pending, 0.6f);
                    bool border = (x + 1 < w && px[x + 1, y].Covered > 0 && px[x + 1, y].Canton != p.Canton)
                                  || (y + 1 < ph && px[x, y + 1].Covered > 0 && px[x, y + 1].Canton != p.Canton);
                    if (border) c = c.Scale(0.62f);
                }
                if (x == cx && y == cy) c = Cursor;
                colors[x, y] = c;
            }

        // labels: the biggest towns first, never overlapping each other or the cursor
        int rows = MapRows;
        var label = new char?[w, rows];
        int budget = z switch { 8 => 10, 4 => 22, 2 => 32, _ => 40 };
        var taken = new bool[w, rows];
        foreach (var town in _country.Towns.Where(t => t.Kind == PlaceKind.Town).OrderByDescending(t => t.Rank))
        {
            if (budget == 0) break;
            var tt = town.Tile;
            int x = FloorDiv(tt.E - _viewLeft, z), y = FloorDiv(_viewTop - tt.N, z) / 2;
            string text = "•" + town.Name;
            if (x < 0 || y < 0 || y >= rows || x + text.Length >= w) continue;
            bool free = true;
            for (int i = -1; i <= text.Length && free; i++)
                if (x + i >= 0 && x + i < w && (taken[x + i, y] || (y == cy / 2 && x + i == cx))) free = false;
            if (!free) continue;
            for (int i = -1; i <= text.Length; i++)
                if (x + i >= 0 && x + i < w) taken[x + i, y] = true;
            for (int i = 0; i < text.Length; i++) label[x + i, y] = text[i];
            budget--;
        }

        var panel = PanelLines();
        var sb = new StringBuilder(_termW * _termH * 24);
        sb.Append("\x1b[H");
        Header(sb);

        Rgb? lastFg = null, lastBg = null;
        for (int row = 0; row < rows; row++)
        {
            lastFg = lastBg = null;
            for (int x = 0; x < w; x++)
            {
                Rgb top = colors[x, row * 2], bottom = colors[x, row * 2 + 1];
                if (label[x, row] is { } ch)
                {
                    var bg = Rgb.Mix(top, bottom, 0.5f).Scale(0.55f);
                    Fg(sb, new Rgb(255, 255, 255), ref lastFg);
                    Bg(sb, bg, ref lastBg);
                    sb.Append(ch);
                }
                else
                {
                    Fg(sb, top, ref lastFg);
                    Bg(sb, bottom, ref lastBg);
                    sb.Append('▀');
                }
            }
            sb.Append("\x1b[0m ");
            AppendPanel(sb, row < panel.Count ? panel[row] : default);
            sb.Append("\x1b[0m\x1b[K\r\n");
        }
        Footer(sb);
        return sb.ToString();
    }

    private void Header(StringBuilder sb)
    {
        var cur = new TileId(_cE, _cN);
        string canton = _country.CantonAt(cur)?.Name ?? (_country.Covered(cur) ? "?" : "outside");
        int elev = _country.MaxElevation(cur);
        string mode = _anchor != null ? (_anchorRemove ? "ERASE RECT" : "RECT")
                    : _brush == Brush.Paint ? $"BRUSH {_brushKm} km" : _brush == Brush.Erase ? $"ERASER {_brushKm} km" : "move";
        string text = $" MapSetup — select a zone │ {_cE}-{_cN}  {canton}"
                      + (elev > 0 ? $"  {elev} m" : "") + $" │ {Zoom} km/px │ {mode}";
        sb.Append("\x1b[48;2;40;44;58m\x1b[38;2;255;255;255m\x1b[1m");
        sb.Append(Fit(text, _termW));
        sb.Append("\x1b[0m\r\n");
    }

    private void Footer(StringBuilder sb)
    {
        string text;
        if (_confirm is { } q) text = " " + q.Question;
        else if (_mode == Mode.Search) text = $" Find place: {_input}_   (↑↓ pick, Enter select, Esc cancel)";
        else if (_mode == Mode.Radius) text = $" {_pickedTown?.Name}: radius in km: {_input}_   (Enter add, Esc cancel)";
        else if (_mode == Mode.Canton) text = " Canton: ↑↓ or a letter, Enter adds it, Esc cancel";
        else if (_message != null) text = " " + _message;
        else text = " ←↑→↓ move (Shift ×10)  +/- zoom  R rect  E erase rect  Space tile  B brush  N eraser  [ ] size  F find  C canton  X clear  H help  Enter continue  Q quit";
        bool alert = _confirm != null || _message != null;
        sb.Append(alert ? "\x1b[48;2;120;90;10m\x1b[38;2;255;255;255m" : "\x1b[48;2;40;44;58m\x1b[38;2;210;210;220m");
        sb.Append(Fit(text, _termW - 1));
        sb.Append("\x1b[0m");
    }

    private readonly record struct PanelLine(string Text, Rgb? Swatch = null, bool Bold = false, bool Highlight = false, string Right = "");

    private List<PanelLine> PanelLines()
    {
        var lines = new List<PanelLine>();
        if (_mode == Mode.Search || _mode == Mode.Radius)
        {
            lines.Add(new("Find a place", Bold: true));
            lines.Add(new($"> {_input}"));
            lines.Add(new(""));
            if (_matches.Count == 0) lines.Add(new(_input.Length == 0 ? "type a town, summit or pass" : "no match"));
            for (int i = 0; i < _matches.Count; i++)
            {
                var m = _matches[i];
                string kind = m.Kind == PlaceKind.Town ? m.Canton : m.Rank > 0 ? $"{m.Rank} m" : "peak";
                lines.Add(new(m.Name, Highlight: i == _pick, Right: kind));
            }
            return lines;
        }
        if (_mode == Mode.Canton)
        {
            lines.Add(new("Add a whole canton", Bold: true));
            lines.Add(new(""));
            int visible = MapRows - 3, first = Math.Clamp(_pick - visible / 2, 0, Math.Max(0, _country.Cantons.Count - visible));
            for (int i = first; i < Math.Min(_country.Cantons.Count, first + visible); i++)
            {
                var c = _country.Cantons[i];
                lines.Add(new($"{c.Code}  {c.Name}", Highlight: i == _pick,
                    Right: $"{_country.TilesInCanton(c.Id).Count():N0} km²"));
            }
            return lines;
        }
        if (_help)
        {
            lines.Add(new("Keys", Bold: true));
            foreach (var (k, d) in new[]
                     {
                         ("←↑→↓", "move one pixel"), ("Shift+arrows", "move ten"), ("+ / -", "zoom in / out"),
                         ("R … R", "add a rectangle"), ("E … E", "erase a rectangle"), ("Esc", "cancel a rectangle"),
                         ("Space", "toggle the pixel"), ("B", "paint while moving"), ("N", "erase while moving"),
                         ("[ / ]", "brush radius (km)"), ("F or /", "find a place + radius"), ("C", "add a canton"),
                         ("X", "clear selection"), ("Enter", "continue"), ("Q", "quit"), ("H or ?", "hide help"),
                     })
                lines.Add(new(k, Right: d));
            return lines;
        }

        lines.Add(new("Selection", Bold: true));
        foreach (var (l, v) in _summaryLines) lines.Add(new(l, Right: v));
        lines.Add(new(""));
        lines.Add(new("Legend", Bold: true));
        lines.Add(new("selected", Selected));
        lines.Add(new("already built", Rgb.Mix(Elevation(800), BuiltColor, 0.28f)));
        lines.Add(new("downloaded, not built", Rgb.Mix(Elevation(800), Downloaded, 0.35f)));
        lines.Add(new("available", Elevation(1200)));
        lines.Add(new("no data (outside CH)", Void));
        lines.Add(new("cursor", Cursor));
        lines.Add(new(""));
        lines.Add(new("H for all keys"));
        return lines;
    }

    private void AppendPanel(StringBuilder sb, PanelLine line)
    {
        int width = PanelW;
        var bg = line.Highlight ? new Rgb(70, 76, 100) : PanelBg;
        sb.Append($"\x1b[48;2;{bg.R};{bg.G};{bg.B}m");
        if (line.Text == null) { sb.Append(new string(' ', width)); return; }

        int used = 0;
        if (line.Swatch is { } s)
        {
            sb.Append($"\x1b[38;2;{s.R};{s.G};{s.B}m██ ");
            used = 3;
        }
        sb.Append(line.Bold ? "\x1b[1m\x1b[38;2;255;255;255m" : $"\x1b[38;2;{Text.R};{Text.G};{Text.B}m");
        string right = line.Right ?? "";
        int room = width - used - (right.Length > 0 ? right.Length + 1 : 0);
        string left = Fit(line.Text, Math.Max(0, room));
        sb.Append(left);
        if (right.Length > 0)
        {
            sb.Append("\x1b[22m");
            sb.Append($"\x1b[38;2;{Dim.R};{Dim.G};{Dim.B}m ");
            sb.Append(Fit(right, width - used - left.Length - 1));
        }
        sb.Append("\x1b[22m");
    }

    private static string Fit(string s, int width)
    {
        if (width <= 0) return "";
        if (s.Length > width) return width > 1 ? s[..(width - 1)] + "…" : s[..width];
        return s.PadRight(width);
    }

    private static void Fg(StringBuilder sb, Rgb c, ref Rgb? last)
    {
        if (last == c) return;
        sb.Append("\x1b[38;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
        last = c;
    }

    private static void Bg(StringBuilder sb, Rgb c, ref Rgb? last)
    {
        if (last == c) return;
        sb.Append("\x1b[48;2;").Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
        last = c;
    }

    private static readonly Stream Stdout = Console.OpenStandardOutput();

    private static void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Stdout.Write(bytes);
        Stdout.Flush();
    }

    // ---- Windows console ------------------------------------------------------------------

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>Windows Terminal has ANSI on already; the classic console host needs asking.</summary>
    private static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var h = GetStdHandle(-11);
        if (GetConsoleMode(h, out uint mode)) SetConsoleMode(h, mode | 0x0004);
    }
}
