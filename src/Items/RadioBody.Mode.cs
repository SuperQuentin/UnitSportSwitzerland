namespace UnitSport.Items;

public partial class RadioBody
{
    /// <summary>
    /// What happens when the CD ends (<see cref="RadioMode"/>), set by the server on request
    /// (<c>RadioManager.SetMode</c>) and carried by the <c>State</c> synchronizer with the CD (#211).
    /// </summary>
    [Godot.Export] public int Mode { get; set; }
}
