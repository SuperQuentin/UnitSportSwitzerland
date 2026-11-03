using Godot;
using UnitSport.Ui;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The Battle Royale's compass strip (#190): the shared <see cref="CompassStrip"/> (#1226) fed from
/// <see cref="BrManager"/>. Markers ride on the strip, and are pinned to its edge when behind you: the safe zone
/// (purple, its distance), the nearest supply drop within 2 km, team-mates, alphorns heard, pings, the Postauto stop
/// while carrying a tag, and your waypoint. Labels are rebuilt only when a rounded distance changes.
/// </summary>
public partial class BrCompass : CompassStrip
{
    public BrCompass(BrManager br) : base(new Source(br)) { }
    public BrCompass() : this(null!) { }

    private sealed class Source : ICompassSource
    {
        private readonly BrManager _br;
        private readonly Dictionary<int, (int Value, string Name, string Text)> _labels = new();

        public Source(BrManager br) => _br = br;

        public bool Shown => _br.State.Running && _br.InMatch && BrPrefs.Current.Compass;
        public Vector2? Heading => _br.ViewPoint()?.Heading;
        public bool Declutter => false;
        public bool Fades => false;

        /// <summary>"<paramref name="name"/> 120 m" (or "120 m" without a name), rebuilt only when the whole metres or the name change.</summary>
        private string Label(int key, string name, float metres)
        {
            int m = (int)Mathf.Round(metres);
            if (_labels.TryGetValue(key, out var l) && l.Value == m && l.Name == name) return l.Text;
            string text = (name.Length > 0 ? name + " " : "") + m.ToString(System.Globalization.CultureInfo.InvariantCulture) + " m";
            _labels[key] = (m, name, text);
            return text;
        }

        public int Fill(CompassMarker[] into)
        {
            if (_br.ViewPoint() is not { } view) return 0;
            int n = 0;
            void Add(int key, Vector2 toward, Color color, MarkerShape shape, int row, string label)
            {
                if (n >= into.Length) return;
                into[n++] = new CompassMarker { Key = key, Toward = toward, Color = color, Shape = shape, Row = (byte)row, Label = label };
            }
            bool own = _br.Watching == 0;
            // the safe zone
            if (_br.ZoneNow is { } z && own)
            {
                var goal = z.Phase == 0 ? z.Centre : z.NextCentre;
                float radius = z.Phase == 0 ? z.Radius : z.NextRadius;
                float dist = view.Position.DistanceTo(goal) - radius;
                if (dist > 0) Add(1, goal - view.Position, BrMapDraw.Storm with { A = 1f }, MarkerShape.Diamond, 0, Label(1, "", dist));
            }
            // the nearest supply drop within 2 km
            if (own)
            {
                Vector2 drop = Vector2.Zero;
                float best = float.MaxValue;
                foreach (var d in BrMapDraw.Airdrops(_br))
                {
                    float dd = d.At.DistanceTo(view.Position);
                    if (dd < best) { best = dd; drop = d.At; }
                }
                if (drop != Vector2.Zero && best < 2000f) Add(2, drop - view.Position, BrMapDraw.Drop, MarkerShape.Down, 2, Label(2, "drop", best));
            }
            if (own)
            {
                int i = 0;
                foreach (var mate in _br.Mates())
                {
                    Add(100 + i, mate.At - view.Position, BrMapDraw.Mate, MarkerShape.Down, 3, Label(100 + i, mate.Name, view.Position.DistanceTo(mate.At)));
                    i++;
                }
            }
            for (int i = 0; i < _br.Horns.Count; i++)
            {
                var horn = _br.Horns[i];
                Add(200 + i, horn.At - view.Position, BrMapDraw.Horn, MarkerShape.Down, 5, Label(200 + i, "alphorn", view.Position.DistanceTo(horn.At)));
            }
            for (int i = 0; i < _br.Pings.Count; i++)
            {
                var ping = _br.Pings[i];
                Add(300 + i, ping.At - view.Position, BrMapDraw.Ping, MarkerShape.Diamond, 4, Label(300 + i, ping.Name, view.Position.DistanceTo(ping.At)));
            }
            // carrying a fallen team-mate's tag (#480): the nearest Postauto stop
            if (own && _br.CarryingTag && _br.NearestStop(view.Position) is { } stop)
                Add(3, stop.At - view.Position, BrMapDraw.Postauto, MarkerShape.Diamond, 6, Label(3, "Postauto", view.Position.DistanceTo(stop.At)));
            if (_br.Waypoint is { } wp && own)
                Add(4, wp - view.Position, BrMapDraw.Waypoint, MarkerShape.Down, 1, Label(4, "", view.Position.DistanceTo(wp)));
            return n;
        }
    }
}
