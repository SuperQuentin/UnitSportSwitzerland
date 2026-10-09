using Godot;

namespace UnitSport.Player;

/// <summary>Being hit by a thrown thing (#261): the body rocks away from the blow, and on the victim's own machine it hurts.</summary>
public partial class FootPlayer
{
    /// <summary>The blow's direction in the body's own frame (flat), and seconds since it landed.</summary>
    private Vector3 _flinchDir;
    private float _flinchStrength, _flinchTime = 10f;

    /// <summary>
    /// Rocks the figure away from a blow coming along <paramref name="push"/> (world, flat): the top
    /// thrown back past upright, then a damped wobble home. Every peer plays it on its own copy.
    /// </summary>
    public void Flinch(Vector3 push, float strength)
    {
        var local = GlobalTransform.Basis.Inverse() * push;
        local.Y = 0;
        _flinchDir = local.LengthSquared() > 1e-4f ? local.Normalized() : Vector3.Back;
        _flinchStrength = Mathf.Clamp(strength, 0f, 1.5f);
        _flinchTime = 0f;
        _face.Hurt();   // and grimaces (#657), on every peer like the rock
    }

    /// <summary>
    /// On the victim's own machine: some health (never the last <see cref="Items.ThrowHits.Floor"/>),
    /// a stagger back along the blow, the screen shaken and the pad rumbled.
    /// </summary>
    public void Bonked(float damage, Vector3 push, long attacker)
    {
        if (_deadTimer > 0 || Ragdolled) return;
        float amount = Mathf.Min(damage, Health - Items.ThrowHits.Floor);
        if (amount > 0) TakeDamage(amount, attacker, DamageCause.Other);
        else Core.PlayerInput.Rumble(0.4f, 0.3f, 0.2f);
        Shaken?.Invoke(Mathf.Clamp(damage / Items.ThrowHits.MaxDamage, 0.25f, 0.8f));
        if (_ride == null)
        {
            // a step back and a little hop: hit, not launched
            Velocity += push * (2.2f + damage * 0.08f) + Vector3.Up * 1.6f;
            DanceId = 0;
        }
    }

    /// <summary>The flinch's rotation about the hips this frame, identity when none is playing.</summary>
    private Transform3D FlinchPose(float dt)
    {
        _flinchTime += dt;
        if (_flinchTime > 1.2f || _flinchStrength <= 0f) return Transform3D.Identity;
        // thrown back hard, then a spring settling: overshoot forward once, smaller back, gone
        float a = _flinchStrength * 0.42f * Mathf.Exp(-5.5f * _flinchTime) * Mathf.Cos(11f * _flinchTime)
                  * Mathf.Min(1f, _flinchTime * 30f);
        // the head goes the way the blow went: rotate about the axis across it
        var axis = Vector3.Up.Cross(_flinchDir).Normalized();
        var hip = new Vector3(0, 0.95f, 0);
        return new Transform3D(Basis.Identity, hip) * new Transform3D(new Basis(axis, a), Vector3.Zero)
               * new Transform3D(Basis.Identity, -hip);
    }
}
