using Godot;

namespace UnitSport.Avatar;

// The birthday hats (#1423, docs/notes/occasions/birthdays.md): the party hat worn on the day and the gift's five yearly collectibles.
// Static geometry drawn with the head like every Headwear: nothing on them moves (the propeller and the flame are still).
public static partial class HumanMeshBuilder
{
    private static readonly Color PartyPink = new(0.93f, 0.36f, 0.62f), PartyBlue = new(0.25f, 0.55f, 0.92f),
        PartyYellow = new(0.98f, 0.82f, 0.22f), PartyGreen = new(0.35f, 0.78f, 0.42f), PartyWhite = new(0.97f, 0.96f, 0.93f),
        Gold = new(0.93f, 0.72f, 0.20f), Flame = new(1f, 0.78f, 0.25f);

    /// <summary>A small ball: two cones point to point.</summary>
    private static void Ball(MeshScratch s, Vector3 c, Vector3 up, float r, Color colour)
    {
        s.Tube(c - up * r, c, 0.002f, r, colour, 6);
        s.Tube(c, c + up * r, r, 0.002f, colour, 6);
    }

    /// <summary>A striped cone, bands of the given colours from the brim up.</summary>
    private static Vector3 StripedCone(MeshScratch s, Vector3 from, Vector3 up, float radius, float height, Color[] bands)
    {
        int n = bands.Length;
        for (int i = 0; i < n; i++)
        {
            float t0 = (float)i / n, t1 = (float)(i + 1) / n;
            s.Tube(from + up * height * t0, from + up * height * t1, radius * (1 - t0) + 0.004f, radius * (1 - t1) + 0.004f, bands[i], 8);
        }
        return from + up * height;
    }

    private static void AppendBirthdayHat(MeshScratch s, Headwear hat, Vector3 top, Vector3 side, Vector3 up, Vector3 fwd, float k, Basis frame)
    {
        switch (hat)
        {
            case Headwear.PartyHat:
            {
                // the paper cone, worn a little to one side, a pompom on the tip
                var tilt = (up + side * 0.18f).Normalized();
                var tip = StripedCone(s, top - up * 0.01f, tilt, 0.085f * k, 0.22f, [PartyPink, PartyYellow, PartyBlue, PartyPink, PartyYellow]);
                Ball(s, tip, up, 0.032f, PartyWhite);
                break;
            }
            case Headwear.BirthdayCrown:
            {
                // a gold paper crown: a band and seven points, a jewel at the front
                s.Tube(top - up * 0.015f, top + up * 0.04f, 0.112f * k, 0.115f * k, Gold, 12);
                for (int i = 0; i < 7; i++)
                {
                    float a = i / 7f * Mathf.Tau;
                    var dir = side * Mathf.Sin(a) + fwd * Mathf.Cos(a);
                    var foot = top + up * 0.04f + dir * 0.108f * k;
                    s.Tube(foot, foot + up * 0.06f + dir * 0.01f, 0.022f, 0.002f, Gold, 4);
                }
                s.Box(top + up * 0.012f + fwd * 0.118f * k, new Vector3(0.03f, 0.03f, 0.012f), new Color(0.85f, 0.12f, 0.25f), frame);
                break;
            }
            case Headwear.CandleHat:
            {
                // a straight band round the hair, a cone over it, one big birthday candle on top, lit
                s.Tube(top - up * 0.03f, top + up * 0.07f, 0.12f * k, 0.118f * k, PartyBlue, 10);
                var tip = StripedCone(s, top + up * 0.07f, up, 0.118f * k, 0.13f, [PartyWhite, PartyBlue, PartyWhite]);
                s.Tube(tip - up * 0.01f, tip + up * 0.09f, 0.018f, 0.018f, PartyPink, 6);
                s.Tube(tip + up * 0.09f, tip + up * 0.10f, 0.002f, 0.002f, new Color(0.15f, 0.12f, 0.1f), 4);
                s.Tube(tip + up * 0.10f, tip + up * 0.145f, 0.014f, 0.001f, Flame, 5);
                break;
            }
            case Headwear.CakeHat:
            {
                // a little two-tier cake, icing between the tiers, three candles
                var sponge = new Color(0.82f, 0.62f, 0.40f);
                s.Tube(top - up * 0.01f, top + up * 0.05f, 0.11f * k, 0.11f * k, sponge, 10);
                s.Tube(top + up * 0.05f, top + up * 0.065f, 0.115f * k, 0.115f * k, PartyWhite, 10);
                s.Tube(top + up * 0.065f, top + up * 0.11f, 0.075f * k, 0.075f * k, PartyPink, 10);
                s.Tube(top + up * 0.11f, top + up * 0.12f, 0.078f * k, 0.078f * k, PartyWhite, 10);
                for (int i = 0; i < 3; i++)
                {
                    float a = i / 3f * Mathf.Tau;
                    var at = top + up * 0.12f + (side * Mathf.Sin(a) + fwd * Mathf.Cos(a)) * 0.04f * k;
                    s.Tube(at, at + up * 0.05f, 0.007f, 0.007f, i == 1 ? PartyBlue : PartyYellow, 4);
                    s.Tube(at + up * 0.05f, at + up * 0.075f, 0.008f, 0.001f, Flame, 4);
                }
                Ball(s, top + up * 0.13f + fwd * 0.05f * k, up, 0.014f, new Color(0.85f, 0.1f, 0.15f));
                break;
            }
            case Headwear.PropellerCap:
            {
                // a beanie in four colours, a stalk and a two-blade propeller
                Color[] quarters = [PartyYellow, PartyBlue, PartyPink, PartyGreen];
                var crown = top + up * 0.075f;
                s.Tube(top - up * 0.02f, top + up * 0.02f, 0.115f * k, 0.112f * k, quarters[0], 10);
                s.Tube(top + up * 0.02f, crown, 0.112f * k, 0.03f, quarters[1], 10);
                s.Box(top + fwd * 0.11f * k + up * 0.0f, new Vector3(0.13f, 0.012f, 0.06f) * k, quarters[2], frame); // the peak
                s.Tube(crown, crown + up * 0.035f, 0.006f, 0.006f, new Color(0.3f, 0.3f, 0.32f), 4);
                var hub = crown + up * 0.04f;
                s.Box(hub, new Vector3(0.20f, 0.008f, 0.035f), PartyPink, new Basis(up, 0.5f) * frame);
                Ball(s, hub + up * 0.008f, up, 0.012f, quarters[0]);
                break;
            }
            case Headwear.BalloonHat:
            {
                // a headband with three balloons on bent wires
                s.Tube(top - up * 0.01f, top + up * 0.015f, 0.113f * k, 0.113f * k, PartyWhite, 10);
                Color[] balloons = [PartyPink, PartyBlue, PartyYellow];
                for (int i = 0; i < 3; i++)
                {
                    float sway = (i - 1) * 0.5f;
                    var root = top + up * 0.01f + side * sway * 0.12f * k;
                    var end = root + up * 0.20f + side * sway * 0.10f;
                    s.Tube(root, end, 0.003f, 0.003f, new Color(0.25f, 0.25f, 0.27f), 3);
                    var c = end + up * 0.05f;
                    s.Tube(c - up * 0.055f, c, 0.004f, 0.045f, balloons[i], 7);
                    s.Tube(c, c + up * 0.05f, 0.045f, 0.004f, balloons[i], 7);
                }
                break;
            }
        }
    }
}
