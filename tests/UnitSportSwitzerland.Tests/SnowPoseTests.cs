using System;
using Godot;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The live skier and boarder (#933): bones keep their lengths whatever the pose, the feet stay on
/// the snow, the plough opens a V, the tuck goes down, a skate pushes out, the pose survives the wire.
/// </summary>
public class SnowPoseTests
{
    private static (Vector3[] J, SnowPose.Ski[] Skis, SnowPose.Pole[] Poles) Skier(SnowPose.Look look)
    {
        var j = new Vector3[20];
        var skis = new SnowPose.Ski[2];
        var poles = new SnowPose.Pole[2];
        SnowPose.Skier(look, j, skis, poles);
        return (j, skis, poles);
    }

    private static readonly SnowPose.Look Stand = new(0f, 0f, 0f, 0f, 0f, false);

    public static TheoryData<float, float, float, float, float, bool> Looks => new()
    {
        { 0f, 0f, 0f, 0f, 0f, false },
        { 1f, 0f, 0f, 0f, 0f, false },
        { 0.4f, 0.8f, 0f, 0f, 0f, false },
        { 0f, -0.9f, 0f, 0f, 1f, false },
        { 0f, 0f, 1f, 0f, 0f, false },
        { 0f, 0.2f, 0f, 1.4f, 0f, false },
        { 0f, 0f, 0f, -1.9f, 0f, false },
        { 0.3f, 0f, 0f, 0f, 0f, true },
    };

    [Theory, MemberData(nameof(Looks))]
    public void The_bones_keep_their_length(float crouch, float lean, float plough, float stride, float absorb, bool air)
    {
        var look = new SnowPose.Look(crouch, lean, plough, stride, absorb, air);
        var (j, _, _) = Skier(look);
        Bones(j);
        var b = new Vector3[20];
        SnowPose.Boarder(look with { Plough = 0f }, Math.Clamp(lean, -1f, 1f), 1f, b);
        Bones(b);
    }

    private static void Bones(Vector3[] j)
    {
        foreach (var (a, b, len) in new[]
        {
            (SnowPose.HipL, SnowPose.KneeL, SnowPose.Thigh), (SnowPose.KneeL, SnowPose.AnkleL, SnowPose.Shin),
            (SnowPose.HipR, SnowPose.KneeR, SnowPose.Thigh), (SnowPose.KneeR, SnowPose.AnkleR, SnowPose.Shin),
            (SnowPose.ShoulderL, SnowPose.ElbowL, SnowPose.UpperArm), (SnowPose.ElbowL, SnowPose.WristL, SnowPose.Forearm),
            (SnowPose.ShoulderR, SnowPose.ElbowR, SnowPose.UpperArm), (SnowPose.ElbowR, SnowPose.WristR, SnowPose.Forearm),
        })
            Assert.InRange(j[a].DistanceTo(j[b]), len - 0.01f, len + 0.01f);
        // knees in front of the line from hip to ankle, never bent backwards
        Assert.True(j[SnowPose.KneeL].Z > Math.Min(j[SnowPose.HipL].Z, j[SnowPose.AnkleL].Z) - 0.02f);
    }

    [Fact]
    public void The_feet_and_skis_stay_on_the_snow_unless_one_is_lifted()
    {
        var (j, skis, poles) = Skier(Stand);
        Assert.Equal(SnowPose.AnkleY, j[SnowPose.AnkleL].Y, 3);
        Assert.Equal(0f, skis[0].Boot.Y, 3);
        Assert.Equal(0f, skis[1].Boot.Y, 3);
        foreach (var p in poles) Assert.True(p.Basket.Y >= 0.029f, $"basket {p.Basket}");
    }

    [Fact]
    public void The_plough_opens_a_V_tails_apart_tips_together()
    {
        var (_, skis, _) = Skier(Stand with { Plough = 1f });
        // −X ski turns its tip toward +X, the +X ski toward −X
        Assert.True(skis[0].Yaw > 0.2f && skis[1].Yaw < -0.2f, $"{skis[0].Yaw} {skis[1].Yaw}");
        float Tip(SnowPose.Ski s) => (s.Boot + new Basis(Vector3.Up, s.Yaw) * new Vector3(0, 0, 0.9f)).X;
        float Tail(SnowPose.Ski s) => (s.Boot + new Basis(Vector3.Up, s.Yaw) * new Vector3(0, 0, -0.8f)).X;
        Assert.True(Tip(skis[1]) - Tip(skis[0]) < Tail(skis[1]) - Tail(skis[0]) - 0.3f);
    }

    [Fact]
    public void The_schuss_goes_down_and_tucks_the_hands_in()
    {
        var (up, _, _) = Skier(Stand);
        var (tuck, _, _) = Skier(Stand with { Crouch = 1f });
        Assert.True(tuck[SnowPose.Hip].Y < up[SnowPose.Hip].Y - 0.15f);
        Assert.True(tuck[SnowPose.HeadTop].Y < up[SnowPose.HeadTop].Y - 0.3f);
        Assert.True(MathF.Abs(tuck[SnowPose.WristR].X) < MathF.Abs(up[SnowPose.WristR].X));
    }

    [Fact]
    public void A_skate_pushes_one_ski_out_and_the_poles_back()
    {
        var (j, skis, poles) = Skier(Stand with { Stride = 1.4f });   // the +X leg, partway
        Assert.True(j[SnowPose.AnkleR].X > 0.25f, $"{j[SnowPose.AnkleR]}");
        Assert.True(skis[1].Yaw > 0.2f, "the pushing ski's tip turned out");
        Assert.True(Math.Abs(j[SnowPose.AnkleL].X) < 0.15f, "the gliding foot stays under the body");
        var (_, _, still) = Skier(Stand);
        Assert.True(poles[0].Basket.Z < still[0].Basket.Z, "the poles push back");
    }

    [Fact]
    public void Inside_a_turn_the_inside_pole_is_planted_ahead()
    {
        // a + lean rolls the head toward −X: −X is the inside
        var (_, _, poles) = Skier(Stand with { Lean = 0.8f });
        Assert.True(poles[0].Basket.Z > poles[1].Basket.Z + 0.3f, $"{poles[0].Basket} {poles[1].Basket}");
    }

    [Fact]
    public void The_boarders_front_foot_stays_strapped_in_while_the_back_one_pushes()
    {
        var b = new Vector3[20];
        SnowPose.Boarder(Stand with { Stride = 1.5f }, 0f, 1f, b);
        // the nose at +X: the +X foot is the front one, in its binding
        Assert.Equal(0.27f, b[SnowPose.AnkleR].X, 3);
        Assert.Equal(0f, b[SnowPose.AnkleR].Z, 3);
        Assert.True(b[SnowPose.AnkleL].Z > 0.2f, "the back foot is off the board, on the toe side");
    }

    [Fact]
    public void On_the_heels_the_boarder_sits_back_and_on_the_toes_leans_over_them()
    {
        var heel = new Vector3[20];
        var toe = new Vector3[20];
        SnowPose.Boarder(Stand, -1f, 1f, heel);
        SnowPose.Boarder(Stand, 1f, 1f, toe);
        Assert.True(heel[SnowPose.Hip].Z < toe[SnowPose.Hip].Z - 0.1f);
        Assert.True(heel[SnowPose.Hip].Y < toe[SnowPose.Hip].Y);
    }

    [Theory, MemberData(nameof(Looks))]
    public void A_pose_survives_the_wire(float crouch, float lean, float plough, float stride, float absorb, bool air)
    {
        var look = new SnowPose.Look(crouch, lean, plough, stride, absorb, air);
        var back = SnowPose.Unpack(SnowPose.Pack(look));
        Assert.Equal(look.Key, back.Key);
    }
}
