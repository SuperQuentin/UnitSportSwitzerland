using Godot;
using UnitSport.Audio;
using UnitSport.Farming;

namespace UnitSport.Player;

/// <summary>
/// A lowered implement works soil only (#494): over a field or open ground it pulls and works; over
/// a road, paving, gravel or rock it rides on the surface, pulling nothing and working nothing, and
/// the driver is told once to raise it (<see cref="Truck.OnSoil"/>).
/// </summary>
public partial class FootPlayer
{
    /// <summary>The "raise it on the road" toast was shown for this stretch off the soil.</summary>
    private bool _offSoilToasted;

    /// <summary>Before the truck's step: is the lowered implement's bar over soil? (cached surface lookup, no allocation)</summary>
    private void SenseSoil(Truck truck)
    {
        if (!truck.Spec.Farm) return;
        if (truck.Implement is not { } imp || !truck.Lowered)
        {
            truck.OnSoil = true;
            _offSoilToasted = false;
            return;
        }
        var bar = ToGlobal(truck.WorkBarNode);
        var surface = Terrain is { } chunks ? Surfaces.At(chunks, bar, false, truck) : Surface.Grass;
        // a field is soil whatever the cover says; asked only off the soil (rare)
        bool soil = IsSoil(surface) || FarmWork.CellAt(bar) != null;
        truck.OnSoil = soil;
        if (soil) { _offSoilToasted = false; return; }
        if (_offSoilToasted || _farmToastCooldown > 0f) return;
        _offSoilToasted = true;
        string what = imp.Tool switch { FarmTool.Plough => "plough", FarmTool.Sow => "drill", FarmTool.Mow => "mower", _ => "implement" };
        FarmToast(surface is Surface.Asphalt or Surface.Gravel ? $"Raise the {what} on the road" : $"Raise the {what}: no soil to work here");
    }

    /// <summary>Open ground a plough, drill or mower can work: grass and the woodland floor. Tarmac, gravel, rock, ice, water, decks are not.</summary>
    public static bool IsSoil(Surface s) => s is Surface.Grass or Surface.Forest;
}
