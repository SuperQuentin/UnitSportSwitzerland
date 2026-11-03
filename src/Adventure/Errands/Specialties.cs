using System;
using UnitSport.Items;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The cantons' specialties (#1267, errands plan 4.3): the trade goods of the Postkarte cards. Twelve items cover
/// all 26 cantons, a canton without its own taking its region's.
/// </summary>
public static class Specialties
{
    public static ItemId For(string canton) => canton switch
    {
        "FR" => ItemId.Cheese,   // Gruyère
        "VS" => ItemId.RacletteCheese,
        "BS" or "BL" => ItemId.BaslerLaeckerli,
        "GR" => ItemId.EngadinerNusstorte,
        "TI" => ItemId.Marroni,
        "ZG" or "AG" => ItemId.ZugerKirschtorte,
        "AI" or "AR" => ItemId.Appenzeller,
        "JU" => ItemId.TeteDeMoine,
        "SG" or "TG" or "SH" or "GL" => ItemId.StGallerBratwurst,
        "VD" or "GE" or "NE" => ItemId.SaucissonVaudois,
        "ZH" or "SO" => ItemId.ZuercherTirggel,
        "OW" or "NW" or "LU" or "BE" or "UR" or "SZ" => ItemId.Sbrinz,
        _ => ItemId.Cheese,
    };

    public static readonly string[] Cantons =
    {
        "ZH", "BE", "LU", "UR", "SZ", "OW", "NW", "GL", "ZG", "FR", "SO", "BS", "BL",
        "SH", "AR", "AI", "SG", "GR", "AG", "TG", "TI", "VD", "VS", "NE", "GE", "JU",
    };

    /// <summary>The cargo and specialty items an errand may hand out (taken back when the card ends).</summary>
    public static bool IsCargo(ItemId id) => id is ItemId.MailSack or ItemId.FerryCrate or ItemId.SupplyPack or ItemId.Postcard;
}
