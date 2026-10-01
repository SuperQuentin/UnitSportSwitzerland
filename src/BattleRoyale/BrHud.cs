using Godot;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The match on screen (CanvasLayer 10):
/// <list type="bullet">
/// <item>the phase line at the top: timer, players alive, your kills;</item>
/// <item>a red edge and a warning while you are outside the zone;</item>
/// <item>the kill feed, top right;</item>
/// <item>armour and ammunition, bottom left;</item>
/// <item>the results table once it ends;</item>
/// <item>who you are watching once you are out.</item>
/// </list>
/// Everything is read from <see cref="BrManager"/> each frame and drawn.
/// </summary>
public partial class BrHud : CanvasLayer
{
    private readonly BrManager _br;
    private View _view = null!;

    public BrHud(BrManager br)
    {
        _br = br;
        Name = "BrHud";
        Layer = 10;
    }

    public override void _Ready()
    {
        _view = new View { Hud = this, MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _Process(double delta) => _view.QueueRedraw();

    public static string Kills(int n) => n == 1 ? "1 kill" : $"{n} kills";

    private static string Clock(double s)
    {
        s = Math.Max(0, s);
        return $"{(int)(s / 60)}:{(int)(s % 60):00}";
    }

    private partial class View : Control
    {
        public BrHud Hud = null!;
        /// <summary>Below the compass strip, while it shows.</summary>
        private float Y0 => Hud._br.InMatch && Hud._br.State.Running ? BrCompass.Top + BrCompass.Height + 50 : 0;
        private static readonly Color Panel = new(0.05f, 0.05f, 0.08f, 0.62f);
        private static readonly Color Gold = new(1f, 0.82f, 0.25f);
        private static readonly Color Danger = new(1f, 0.25f, 0.2f);
        private static readonly Color Zone = new(0.72f, 0.5f, 1f);

        public override void _Draw()
        {
            var br = Hud._br;
            var s = br.State;
            if (s.Phase == BrPhase.Idle) return;
            var font = ThemeDB.FallbackFont;
            float w = Size.X;
            var me = br.LocalPlayer();
            var mine = br.MyEntry;
            double now = ClockSync.ServerNow;

            // ---- the top line --------------------------------------------------------------
            string top, sub = "";
            switch (s.Phase)
            {
                case BrPhase.Lobby:
                    top = $"BATTLE ROYALE · {s.AreaName} · {s.Entrants.Count} joined";
                    sub = mine == null ? "Type /br join to play" : $"Waiting for players ({BrManager.MinPlayers} to start)";
                    break;
                case BrPhase.Countdown:
                    top = $"BATTLE ROYALE · {s.AreaName} · starts in {Clock(s.CountdownEnds - now)}";
                    sub = mine == null ? $"{s.Entrants.Count} players · /br join" : $"{s.Entrants.Count} players";
                    break;
                case BrPhase.Playing when br.Aboard && s.Flight is { } plane:
                    // in the cargo plane's hold (#207)
                    top = now < plane.OpensAt
                        ? $"IN THE PLANE · doors open over {s.AreaName} in {Clock(plane.OpensAt - now)}"
                        : $"DOORS OPEN · jump before they close in {Clock(plane.ClosesAt - now)}";
                    sub = Core.InputHints.Prompt(Core.PlayerInput.InteractMount, "jump (wingsuit)") + "   ·   in the air "
                        + Core.InputHints.Prompt(Core.PlayerInput.Jump, "opens the parachute") + $"   ·   {s.Entrants.Count(e => !e.Jumped)} aboard";
                    break;
                case BrPhase.Playing when br.ZoneNow is { } z:
                    string zone = z.Phase == 0 ? $"Zone appears in {Clock(z.Left)}"
                        : z.Over ? "Final zone"
                        : z.Shrinking ? $"Zone closing · {Clock(z.Left)}"
                        : $"Zone {z.Phase}/{ZoneSchedule.Phases} shrinks in {Clock(z.Left)}";
                    top = $"{zone}   ·   {s.AliveCount} alive" + (mine != null ? $"   ·   {Kills(mine.Kills)}" : "");
                    sub = s.AreaName;
                    break;
                case BrPhase.Ended:
                    top = $"{s.Entrants.FirstOrDefault(e => e.Peer == s.Winner)?.Name ?? "Nobody"} wins in {s.AreaName}";
                    break;
                default:
                    top = "BATTLE ROYALE";
                    break;
            }
            Banner(font, new Vector2(w * 0.5f, Y0 + 26), top, 20, Colors.White);
            if (sub.Length > 0) Text(font, new Vector2(w * 0.5f, Y0 + 52), sub, 14, new Color(1, 1, 1, 0.75f), HorizontalAlignment.Center);

            // ---- the zone, from where this player stands -----------------------------------
            if (s.Phase == BrPhase.Playing && br.ZoneNow is { } zn && me != null && br.InMatch && br.MeAlive)
            {
                var at = br.ZonePoint(me.GlobalPosition);
                // no warning where the zone does no harm yet (the plane starts outside the square)
                bool outside = zn.Outside(at) && zn.Dps > 0 && !br.Aboard;
                if (outside)
                {
                    // a red edge, pulsing
                    float pulse = 0.25f + 0.15f * Mathf.Sin((float)Time.GetTicksMsec() / 180f);
                    var red = new Color(1f, 0.1f, 0.05f, pulse);
                    float e = 26f;
                    DrawRect(new Rect2(0, 0, w, e), red);
                    DrawRect(new Rect2(0, Size.Y - e, w, e), red);
                    DrawRect(new Rect2(0, 0, e, Size.Y), red);
                    DrawRect(new Rect2(w - e, 0, e, Size.Y), red);
                    Banner(font, new Vector2(w * 0.5f, Y0 + 92), $"OUTSIDE THE ZONE  −{zn.Dps:F0} HP/s", 18, Danger);
                }
                // armour and the held gun's rounds, bottom left
                var inv = br.Inventory();
                string kit = me.Armor > 0 ? $"Armour {me.Armor:F0}" : "";
                if (inv != null && Weapons.Get(inv.HeldId) is { Melee: false } wpn)
                {
                    int n = 0;
                    for (int i = 0; i < Inventory.Size; i++) if (inv[i].Id == wpn.Ammo) n += inv[i].Count;
                    kit += (kit.Length > 0 ? "   ·   " : "") + $"{ItemDefs.Get(wpn.Id)?.Name}: {n}";
                }
                if (kit.Length > 0) Text(font, new Vector2(20, Size.Y - 110), kit, 15, Colors.White, HorizontalAlignment.Left, shadow: true);
            }

            // ---- out of it: whom you are watching ------------------------------------------
            if (br.Watching != 0)
                Banner(font, new Vector2(w * 0.5f, Size.Y - 70),
                    $"Spectating {s.Find(br.Watching)?.Name ?? "?"}   ·   ← / → to switch", 16, Colors.White);

            // ---- kill feed -----------------------------------------------------------------
            double t = Time.GetTicksMsec() / 1000.0;
            float y = Minimap.Margin + Minimap.Side + 24;
            foreach (var line in br.Feed)
            {
                double age = t - line.At;
                if (age > 9) continue;
                float a = (float)Math.Clamp(9 - age, 0, 1);
                var col = line.Mine ? Gold : Colors.White;
                Text(font, new Vector2(w - 20, y), line.Text, 15, col with { A = a }, HorizontalAlignment.Right, shadow: true);
                y += 22;
            }

            // ---- results -------------------------------------------------------------------
            if (s.Phase == BrPhase.Ended) Results(font, s);
        }

        private void Results(Font font, BrState s)
        {
            var rows = s.Entrants.OrderBy(e => e.Place == 0 ? 999 : e.Place).Take(12).ToList();
            float rw = 560, rh = 70 + rows.Count * 24;
            var r = new Rect2((Size.X - rw) * 0.5f, Size.Y * 0.5f - rh * 0.5f, rw, rh);
            DrawRect(r, Panel);
            Text(font, new Vector2(Size.X * 0.5f, r.Position.Y + 30), "RESULTS", 20, Gold, HorizontalAlignment.Center);
            float y = r.Position.Y + 60;
            foreach (var e in rows)
            {
                var col = e.Peer == Hud._br.Me ? Gold : Colors.White;
                Text(font, new Vector2(r.Position.X + 24, y), $"#{e.Place}", 15, col, HorizontalAlignment.Left);
                Text(font, new Vector2(r.Position.X + 80, y), e.Name, 15, col, HorizontalAlignment.Left);
                Text(font, new Vector2(r.Position.X + 330, y), Kills(e.Kills), 15, col, HorizontalAlignment.Left);
                Text(font, new Vector2(r.Position.X + 420, y), $"{e.Damage:F0} dmg", 15, col, HorizontalAlignment.Left);
                Text(font, new Vector2(r.End.X - 20, y), Clock(e.Survived), 15, col, HorizontalAlignment.Right);
                y += 24;
            }
        }

        /// <summary>Centred text on a dark band.</summary>
        private void Banner(Font font, Vector2 centre, string text, int size, Color color)
        {
            var sz = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
            DrawRect(new Rect2(centre.X - sz.X * 0.5f - 12, centre.Y - size - 2, sz.X + 24, size + 12), Panel);
            DrawString(font, new Vector2(centre.X - sz.X * 0.5f, centre.Y), text, HorizontalAlignment.Left, -1, size, color);
        }

        private void Text(Font font, Vector2 at, string text, int size, Color color, HorizontalAlignment align, bool shadow = false)
        {
            float width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
            float x = align switch { HorizontalAlignment.Center => at.X - width * 0.5f, HorizontalAlignment.Right => at.X - width, _ => at.X };
            if (shadow) DrawString(font, new Vector2(x + 1, at.Y + 1), text, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, color.A * 0.8f));
            DrawString(font, new Vector2(x, at.Y), text, HorizontalAlignment.Left, -1, size, color);
        }
    }
}
