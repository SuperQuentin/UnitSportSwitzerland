using UnitSport.Core;

namespace UnitSport.Occasions.Festivals;

public static partial class FestivalCatalog
{
    /// <summary>
    /// <c>--festival test</c>: the check-only festival exists in this run (an occasion entry, a catalogue def, its bell and gong).
    /// Never part of the game: no menu, no setting, no default entry sets it.
    /// </summary>
    public static bool TestRequested => CmdArgs.Value("--festival") == TestId;
}
