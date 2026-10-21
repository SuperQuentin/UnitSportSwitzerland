using System.Globalization;
using System.Text;

namespace UnitSport.Playtest.TopView;

/// <summary>
/// <see cref="TopViewShapes"/> as an SVG (#844). For a PNG, Godot rasterises it with ThorVG
/// (<c>Image.LoadSvgFromString</c>), which draws no <c>&lt;text&gt;</c> headless: with
/// <c>pixelText</c> the labels are a 5×7 pixel font drawn as paths instead. The HTML page keeps real
/// text, which the browser draws.
/// </summary>
public static class TopViewSvg
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The shapes as an SVG <paramref name="px"/> square. With <paramref name="ground"/> (#851) the
    /// background is left clear for its raster, drawn under it (<c>TopViewImage</c>, the HTML page),
    /// and its roads, rails and rivers come first, at their width.
    /// </summary>
    public static string Render(TopViewShapes s, int px, bool pixelText, TopGround? ground = null)
    {
        var sb = new StringBuilder(16 * 1024);
        double k = px / s.Side;
        (double X, double Y) P(EN p) => ((p.E - s.Min.E) * k, (s.Min.N + s.Side - p.N) * k);
        string F(double v) => v.ToString("0.#", Inv);

        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{px}\" height=\"{px}\" viewBox=\"0 0 {px} {px}\">");
        if (ground == null) sb.Append($"<rect width=\"{px}\" height=\"{px}\" fill=\"#f4f1ea\"/>");
        else Roads(sb, ground.Roads, P, k);

        // a grid every scale-bar length, on round LV95 metres
        double bar = TopViewScene.ScaleBar(s.Side);
        sb.Append(ground == null ? "<g stroke=\"#ddd8cc\" stroke-width=\"1\">" : "<g stroke=\"#ffffff\" stroke-opacity=\"0.35\" stroke-width=\"1\">");
        for (double e = Math.Ceiling(s.Min.E / bar) * bar; e <= s.Min.E + s.Side; e += bar)
        {
            double x = (e - s.Min.E) * k;
            sb.Append($"<line x1=\"{F(x)}\" y1=\"0\" x2=\"{F(x)}\" y2=\"{px}\"/>");
        }
        for (double n = Math.Ceiling(s.Min.N / bar) * bar; n <= s.Min.N + s.Side; n += bar)
        {
            double y = (s.Min.N + s.Side - n) * k;
            sb.Append($"<line x1=\"0\" y1=\"{F(y)}\" x2=\"{px}\" y2=\"{F(y)}\"/>");
        }
        sb.Append("</g>");

        foreach (var t in s.Tracks)
        {
            sb.Append($"<polyline fill=\"none\" stroke=\"{t.Color.Hex}\" stroke-width=\"{(t.Minor ? 1 : t.Server ? 1.5 : 2)}\" stroke-opacity=\"{(t.Minor ? 0.5 : 0.8)}\"{(t.Server ? " stroke-dasharray=\"5 4\"" : "")} points=\"");
            foreach (var p in t.Points)
            {
                var (x, y) = P(p);
                sb.Append(F(x)).Append(',').Append(F(y)).Append(' ');
            }
            sb.Append("\"/>");
            if (t.Minor) continue;
            foreach (var p in t.Ticks)
            {
                var (x, y) = P(p);
                sb.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"2.2\" fill=\"{t.Color.Hex}\"/>");
            }
        }

        foreach (var v in s.Vectors)
        {
            var (x1, y1) = P(v.From);
            var (x2, y2) = P(v.To);
            sb.Append($"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"#333\" stroke-width=\"1.5\"/>");
            sb.Append($"<circle cx=\"{F(x2)}\" cy=\"{F(y2)}\" r=\"2\" fill=\"#333\"/>");
        }

        var labels = new List<(double X, double Y, string Text, string Color)>();
        // the notes first: what stood there is drawn over them
        int pin = 0;
        foreach (var p in s.Pins)
        {
            var (x, y) = P(p.At);
            pin++;
            sb.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"7\" fill=\"#e03131\" stroke=\"#fff\" stroke-width=\"2\"/>");
            labels.Add((x + 10, y - 8, $"NOTE {pin}: {Clip(p.Label, 48)}", "#e03131"));
        }

        foreach (var b in s.Boxes)
        {
            var fwd = EN.Bearing(b.Heading);
            var right = new EN(fwd.N, -fwd.E);
            var corners = new[] { (1, 1), (1, -1), (-1, -1), (-1, 1) }
                .Select(c => P(b.Center + fwd * (c.Item1 * b.Along / 2) + right * (c.Item2 * b.Across / 2))).ToArray();
            sb.Append($"<polygon points=\"{string.Join(" ", corners.Select(c => F(c.X) + "," + F(c.Y)))}\" "
                + (b.Server ? $"fill=\"none\" stroke=\"{b.Color.Hex}\" stroke-dasharray=\"4 3\"" : $"fill=\"{b.Color.Hex}\" fill-opacity=\"0.35\" stroke=\"{b.Color.Hex}\"")
                + " stroke-width=\"1.5\"/>");
            // the front edge, thicker: which way it faces
            sb.Append($"<line x1=\"{F(corners[0].X)}\" y1=\"{F(corners[0].Y)}\" x2=\"{F(corners[1].X)}\" y2=\"{F(corners[1].Y)}\" stroke=\"{b.Color.Hex}\" stroke-width=\"3\"/>");
            var (lx, ly) = P(b.Center);
            if (!b.Server && b.Label.Length > 0) labels.Add((lx + 6, ly - 6, b.Label, "#222"));
        }

        foreach (var a in s.Arrows)
        {
            var (x, y) = P(a.At);
            double len = Math.Max(18, 2.5 * k);
            double h = a.Heading;
            // screen y grows south: a bearing's screen direction is (sin h, -cos h)
            (double X, double Y) D(double ang, double r) => (x + Math.Sin(ang) * r, y - Math.Cos(ang) * r);
            var tip = D(h, len * 0.6);
            var l = D(h + 2.5, len * 0.45);
            var r2 = D(h - 2.5, len * 0.45);
            sb.Append($"<polygon points=\"{F(tip.X)},{F(tip.Y)} {F(l.X)},{F(l.Y)} {F(x)},{F(y)} {F(r2.X)},{F(r2.Y)}\" "
                + (a.Server ? $"fill=\"none\" stroke=\"{a.Color.Hex}\" stroke-width=\"1.5\"" : $"fill=\"{a.Color.Hex}\" stroke=\"#fff\" stroke-width=\"1\"") + "/>");
            if (!a.Server) labels.Add((x + 8, y + 14, a.Label, a.Color.Hex));
        }

        foreach (var o in s.Offsets)
        {
            var (x1, y1) = P(o.Client);
            var (x2, y2) = P(o.Server);
            sb.Append($"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{TopViewScene.ServerColor.Hex}\" stroke-width=\"2\" stroke-dasharray=\"2 2\"/>");
            if (o.Metres >= 0.05) labels.Add(((x1 + x2) / 2 + 6, (y1 + y2) / 2 + 4, $"Δ{o.Metres.ToString("0.0", Inv)} m", TopViewScene.ServerColor.Hex));
        }

        foreach (var (x, y, text, color) in labels) Text(sb, x, y, text, 11, color, pixelText);

        // scale bar, north arrow, the window
        double barPx = bar * k;
        sb.Append($"<rect x=\"12\" y=\"{px - 22}\" width=\"{F(barPx)}\" height=\"6\" fill=\"#222\"/>");
        Text(sb, 12, px - 28, bar >= 1000 ? $"{F(bar / 1000)} km" : $"{F(bar)} m", 11, "#222", pixelText);
        sb.Append($"<polygon points=\"{px - 24},14 {px - 31},34 {px - 24},29 {px - 17},34\" fill=\"#222\"/>");
        Text(sb, px - 28, 50, "N", 11, "#222", pixelText);
        string title = s.Title.Length > 0 ? s.Title
            : $"t {s.T0.ToString("0.0", Inv)}-{s.T1.ToString("0.0", Inv)} s, at {s.At.ToString("0.0", Inv)} s, {F(s.Side)} m";
        Text(sb, 12, 22, title, 12, "#222", pixelText);
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>The ground's lines, casings first: a road is its fill between two darker edges.</summary>
    private static void Roads(StringBuilder sb, List<TopRoad> roads, Func<EN, (double X, double Y)> P, double k)
    {
        string Points(TopRoad r) => string.Join(" ", r.Points.Select(p => { var (x, y) = P(p); return x.ToString("0.#", Inv) + "," + y.ToString("0.#", Inv); }));
        double Px(TopRoad r) => Math.Max(1.2, r.Width * k);
        sb.Append("<g fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
        foreach (var r in roads.Where(r => r.Casing != null))
            sb.Append($"<polyline stroke=\"{r.Casing}\" stroke-width=\"{(Px(r) + 2).ToString("0.#", Inv)}\" points=\"{Points(r)}\"/>");
        foreach (var r in roads)
            sb.Append($"<polyline stroke=\"{r.Fill}\" stroke-width=\"{Px(r).ToString("0.#", Inv)}\"{(r.Dashed ? $" stroke-dasharray=\"{(Px(r) * 3).ToString("0.#", Inv)} {(Px(r) * 2).ToString("0.#", Inv)}\" stroke-linecap=\"butt\"" : "")} points=\"{Points(r)}\"/>");
        sb.Append("</g>");
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>A label, on a pale backing so it reads over the tracks: SVG text, or the pixel font.</summary>
    private static void Text(StringBuilder sb, double x, double y, string text, double size, string color, bool pixel)
    {
        double cell = size / 7;   // one font pixel
        double width = text.Length * 6 * cell;
        sb.Append($"<rect x=\"{(x - 2).ToString("0.#", Inv)}\" y=\"{(y - size - 1).ToString("0.#", Inv)}\" width=\"{(width + 4).ToString("0.#", Inv)}\" height=\"{(size + 4).ToString("0.#", Inv)}\" fill=\"#fff\" fill-opacity=\"0.7\"/>");
        if (!pixel)
        {
            string escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            sb.Append($"<text x=\"{x.ToString("0.#", Inv)}\" y=\"{y.ToString("0.#", Inv)}\" font-family=\"monospace\" font-size=\"{size.ToString("0.#", Inv)}\" fill=\"{color}\">{escaped}</text>");
            return;
        }
        sb.Append($"<path fill=\"{color}\" d=\"");
        double top = y - size;
        double cx = x;
        foreach (char ch in Fold(text))
        {
            string rows = PixelFont.Glyph(ch);
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                {
                    if (rows[row * 5 + col] != '1') continue;
                    int run = 1;
                    while (col + run < 5 && rows[row * 5 + col + run] == '1') run++;
                    sb.Append('M').Append((cx + col * cell).ToString("0.##", Inv)).Append(' ').Append((top + row * cell).ToString("0.##", Inv))
                        .Append('h').Append((run * cell).ToString("0.##", Inv)).Append('v').Append(cell.ToString("0.##", Inv))
                        .Append('h').Append((-run * cell).ToString("0.##", Inv)).Append('z');
                    col += run - 1;
                }
            cx += 6 * cell;
        }
        sb.Append("\"/>");
    }

    /// <summary>Upper case, accents dropped: what the pixel font has.</summary>
    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// A page with a slider over the window (#844, <c>--topview ... --html</c>): one SVG per step,
    /// drawn in advance, swapped as the slider moves. Self-contained, opened from the disk.
    /// </summary>
    public static string Html(string title, IReadOnlyList<(double T, string Svg)> steps, string? groundPngBase64 = null)
    {
        var sb = new StringBuilder(steps.Sum(s => s.Svg.Length) + 4096);
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Top view</title><style>body{{font:14px system-ui,sans-serif;margin:16px;background:#faf8f3;color:#222}}"
            + ".step{display:none}.step.on{display:block}svg{display:block;max-width:100%;height:auto}"
            + "#map{display:inline-block;border:1px solid #ccc;background-size:100% 100%;max-width:100%}"
            + "input[type=range]{width:100%;max-width:900px}#t{font-variant-numeric:tabular-nums}</style></head><body>");
        sb.Append($"<h3>{System.Net.WebUtility.HtmlEncode(title)}</h3>");
        sb.Append($"<p><input id=\"s\" type=\"range\" min=\"0\" max=\"{Math.Max(0, steps.Count - 1)}\" value=\"{Math.Max(0, steps.Count - 1)}\"> <span id=\"t\"></span></p>");
        sb.Append(groundPngBase64 == null ? "<div id=\"map\">" : $"<div id=\"map\" style=\"background-image:url(data:image/png;base64,{groundPngBase64})\">");
        for (int i = 0; i < steps.Count; i++)
            sb.Append($"<div class=\"step\" data-t=\"{steps[i].T.ToString("0.0", Inv)}\">{steps[i].Svg}</div>");
        sb.Append("</div>");
        sb.Append("<script>const s=document.getElementById('s'),t=document.getElementById('t'),d=[...document.querySelectorAll('.step')];"
            + "function show(){d.forEach((e,i)=>e.classList.toggle('on',i==s.value));t.textContent=d.length?'t = '+d[s.value].dataset.t+' s':'empty';}"
            + "s.oninput=show;document.onkeydown=e=>{if(e.key=='ArrowLeft')s.value--;if(e.key=='ArrowRight')s.value++;show();};show();</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}

/// <summary>A 5×7 pixel font: 35 bits a glyph, row by row, for labels ThorVG can draw (it draws no SVG text).</summary>
internal static class PixelFont
{
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['A'] = "01110100011000111111100011000110001", ['B'] = "11110100011000111110100011000111110",
        ['C'] = "01110100011000010000100001000101110", ['D'] = "11100100101000110001100011001011100",
        ['E'] = "11111100001000011110100001000011111", ['F'] = "11111100001000011110100001000010000",
        ['G'] = "01110100011000010111100011000101111", ['H'] = "10001100011000111111100011000110001",
        ['I'] = "01110001000010000100001000010001110", ['J'] = "00111000100001000010000101001001100",
        ['K'] = "10001100101010011000101001001010001", ['L'] = "10000100001000010000100001000011111",
        ['M'] = "10001110111010110101100011000110001", ['N'] = "10001100011100110101100111000110001",
        ['O'] = "01110100011000110001100011000101110", ['P'] = "11110100011000111110100001000010000",
        ['Q'] = "01110100011000110001101011001001101", ['R'] = "11110100011000111110101001001010001",
        ['S'] = "01111100001000001110000010000111110", ['T'] = "11111001000010000100001000010000100",
        ['U'] = "10001100011000110001100011000101110", ['V'] = "10001100011000110001100010101000100",
        ['W'] = "10001100011000110101101011010101010", ['X'] = "10001100010101000100010101000110001",
        ['Y'] = "10001100011000101010001000010000100", ['Z'] = "11111000010001000100010001000011111",
        ['0'] = "01110100011001110101110011000101110", ['1'] = "00100011000010000100001000010001110",
        ['2'] = "01110100010000100010001000100011111", ['3'] = "11111000100010000010000011000101110",
        ['4'] = "00010001100101010010111110001000010", ['5'] = "11111100001111000001000011000101110",
        ['6'] = "00110010001000011110100011000101110", ['7'] = "11111000010001000100010000100001000",
        ['8'] = "01110100011000101110100011000101110", ['9'] = "01110100011000101111000010001001100",
        ['.'] = "00000000000000000000000000110001100", [','] = "00000000000000000000011000010001000",
        [':'] = "00000011000110000000011000110000000", ['-'] = "00000000000000011111000000000000000",
        ['+'] = "00000001000010011111001000010000000", ['/'] = "00000000010001000100010001000000000",
        ['#'] = "01010010101111101010111110101001010", ['('] = "00010001000100001000010000010000010",
        [')'] = "01000001000001000010000100010001000", ['%'] = "11000110010001000100010001001100011",
        ['?'] = "01110100010000100010001000000000100", ['!'] = "00100001000010000100001000000000100",
        ['_'] = "00000000000000000000000000000011111", ['='] = "00000000001111100000111110000000000",
        ['\''] = "00100001000100000000000000000000000", ['>'] = "01000001000001000001000100010001000",
        ['<'] = "00010001000100010000010000010000010", ['*'] = "00000001001010101110101010010000000",
        ['Δ'] = "00100001000101001010100011000111111", ['·'] = "00000000000000001100011000000000000",
        ['…'] = "00000000000000000000000001010110101", ['"'] = "01010010100000000000000000000000000",
        ['@'] = "01110100011011110101101111000001110", ['±'] = "00100001001111100100001000000011111",
        [' '] = "00000000000000000000000000000000000",
    };

    public static string Glyph(char c) =>
        Glyphs.TryGetValue(c, out var g) ? g : c is '–' or '—' ? Glyphs['-'] : Glyphs['?'];
}
