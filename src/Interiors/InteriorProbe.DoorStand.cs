using System.Globalization;
using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--interiorcheck,shot.png --doorstand</c> (#1765, windowed): after walking in, the player stands in the open
/// door's frame itself, first person, a step either side of the threshold and right on it, facing into the
/// building and out to the street, from the street and from inside. Standing in the frame left only the sky gradient on screen (the town, the
/// ground and the interior gone) when the doorway lens's near and far planes were reset under it. Every
/// picture must show something: not one flat colour, not mostly black, and not the sky alone (every row of the
/// picture one colour across). Run it with the horizon at its default (<c>--horizon 60</c>): the test profile's
/// short horizon hides a far/near ratio the depth buffer cannot hold. Each pose is saved beside the shot.
/// </summary>
public partial class InteriorProbe
{
    /// <summary>Where the body stands across the threshold, from the street: + is out, metres from the facade.</summary>
    private static readonly float[] StandOffsets = { 0.3f, 0.1f, 0f, -0.1f, -0.3f };
    /// <summary>Where the body stands from inside, having walked back out to the door: metres in from the doorway.</summary>
    private static readonly float[] InsideDepths = { 0.3f, 0.1f, 0f };
    private int _stand;
    private double _standT;

    /// <summary>Null while under way, then whether it finished.</summary>
    private bool? DoorStand(InteriorManager interiors, double delta)
    {
        _standT += delta;
        // inside first, while the interior the player walked into is the current one; then from the street
        int inside = InsideDepths.Length * 2, poses = inside + StandOffsets.Length * 2;
        if (_stand == 0)
        {
            _player!.DebugThirdPerson(false);
            _stand = 1;
            _standT = 0;
            return null;
        }
        if (_stand > poses) return true;
        int i = _stand - 1;
        bool fromInside = i < inside;
        int j = fromInside ? i : i - inside;
        float offset = fromInside ? InsideDepths[j / 2] : StandOffsets[j / 2];
        bool facingIn = j % 2 == 0;
        if (_standT < 0.05)
        {
            float yaw;
            if (fromInside)
            {
                if (!_player!.Indoors || interiors.Current is not { } layout || interiors.CurrentNode is not { } node)
                {
                    Check(false, "--doorstand: indoors after walking in");
                    return true;
                }
                var way = layout.EntranceFor(_door.Key.ToString());
                var at = node.GlobalTransform * new Vector3(way.X + way.InX * offset, 0.1f, way.Z + way.InZ * offset);
                var inward = node.GlobalTransform.Basis * new Vector3(way.InX, 0, way.InZ);
                var face = facingIn ? inward : -inward;
                yaw = Mathf.Atan2(-face.X, -face.Z);
                _player.EnterInterior(layout.Key, at, yaw);
            }
            else
            {
                // on the doorstep's line, the body's centre at the offset; the game decides which side it is on
                var face = facingIn ? -_door.Outward : _door.Outward;
                yaw = Mathf.Atan2(-face.X, -face.Z);
                if (_player!.Indoors) interiors.Leave(_player);
                _player.LeaveInterior(_door.World + _door.Outward * offset + Vector3.Up * 0.05f, yaw);
            }
            _player.Velocity = Vector3.Zero;
            _player.LookYaw = yaw;
            _player.LookPitch = 0f;
        }
        if (_standT < 1.2) return null;
        string name = $"_stand_{(fromInside ? "inside" : "street")}_{offset.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}_{(facingIn ? "in" : "out")}";
        var cam = GetViewport().GetCamera3D();
        GD.Print($"[interior] stand {name}: indoors {_player!.Indoors}, near {cam?.Near:F3} far {cam?.Far:F0}"
            + (interiors.Links.TryGetValue(_door.Key.ToString(), out var link) ? $", shown {interiors.Portals?.IsShown(link)}" : ""));
        StandPicture(name);
        _stand++;
        _standT = 0;
        return null;
    }

    /// <summary>The door camera's judgement (<see cref="Picture"/>), and the sky alone: nearly every row one colour.</summary>
    private void StandPicture(string suffix)
    {
        if (_shot == null || DisplayServer.GetName() == "headless")
        {
            Check(false, "--doorstand needs a window and a shot path (--interiorcheck,shot.png)");
            return;
        }
        Picture(suffix, strict: true);
        var image = GetViewport().GetTexture().GetImage();
        image.Resize(64, 36, Image.Interpolation.Bilinear);
        int flatRows = 0;
        for (int y = 0; y < 36; y++)
        {
            float lo = 1f, hi = 0f;
            for (int x = 0; x < 64; x++)
            {
                float l = image.GetPixel(x, y).Luminance;
                lo = Mathf.Min(lo, l);
                hi = Mathf.Max(hi, l);
            }
            if (hi - lo < 0.03f) flatRows++;
        }
        Check(flatRows < 33, $"door frame {suffix}: not the sky alone ({flatRows} of 36 rows one colour)");
    }
}
