using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The medic armband's rules (src/Combat/MedicRules.cs, linked in, #218).</summary>
public class MedicRulesTests
{
    [Theory]
    [InlineData(true, false, false, true)]    // two non-medics with PvP on
    [InlineData(true, true, false, false)]    // a medic shoots
    [InlineData(true, false, true, false)]    // a medic is shot
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]  // PvP off hurts nobody
    public void WhoHurtsWhom(bool pvp, bool attackerMedic, bool victimMedic, bool hurts) =>
        Assert.Equal(hurts, MedicLedger.Hurts(pvp, attackerMedic, victimMedic));

    [Fact]
    public void CooldownAfterAnAttack_ByIdentity_RestartsOnEachAttack()
    {
        var l = new MedicLedger { Cooldown = 300 };
        Assert.Null(l.Refusal("Anna", false, 0));
        l.Attacked("Anna", 100);
        Assert.Equal("Medic available in 5:00", l.Refusal("Anna", false, 100));
        Assert.Equal("Medic available in 0:01", l.Refusal("anna", false, 399.5));   // a rejoin under the same name, any case
        Assert.Null(l.Refusal("Anna", false, 400));
        l.Attacked("Anna", 350);                                                    // a new attack restarts it
        Assert.Equal(250, l.CooldownLeft("Anna", 400), 3);
        Assert.Null(l.Refusal("Bert", false, 400));                                 // being attacked does not block it
    }

    [Fact]
    public void BattleRoyaleRefusesIt() =>
        Assert.Equal("No medic armband in a Battle Royale match.", new MedicLedger().Refusal("Anna", true, 0));

    [Fact]
    public void DelayIsCancelledByPlayerDamageTakenAfterItStarted()
    {
        var l = new MedicLedger();
        l.Hurt(7, 10);
        Assert.False(l.HurtSince(7, 12));   // hurt before the delay began: no matter
        l.Hurt(7, 15);
        Assert.True(l.HurtSince(7, 12));
        l.Left(7);
        Assert.False(l.HurtSince(7, 12));
    }

    [Fact]
    public void AvailableInRoundsUp() => Assert.Equal("Medic available in 1:05", MedicLedger.AvailableIn(64.2));
}
