using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// LNF code (BLW "Nutzungsart", MGDM 153.1) to <see cref="CropKind"/> (#494). Generated once from
/// the catalogue <c>LWB_Nutzungsflaechen_Kataloge_V3_0.xml</c> (the v2.0 catalogue holds the same 174
/// codes; only a few German names differ) and committed: the German name is the comment. In both
/// model versions an object's <c>Nutzungsart</c> reference IS the code (v2.0 catalogue TIDs were made
/// equal to the codes in 2023), so no catalogue is read at build time.
///
/// <para>
/// <see cref="CropKind.None"/> means "known, not a farm field here": vines, orchards, berries, nurseries,
/// greenhouses, hedges, trees, litter, forest, unproductive and non-farm ground (TLM already draws vines
/// and orchards), and the overlaying codes (9xx trees, 950/951 strips) that lie on top of a real use.
/// A code missing from the table is counted as unmapped in the report.
/// </para>
/// </summary>
public static class FieldCodes
{
    public static readonly IReadOnlyDictionary<int, CropKind> Table = new Dictionary<int, CropKind>
    {
        [501] = CropKind.Barley, // Sommergerste
        [502] = CropKind.Barley, // Wintergerste
        [504] = CropKind.Barley, // Hafer
        [505] = CropKind.Wheat, // Triticale
        [506] = CropKind.Barley, // Mischel Futtergetreide
        [507] = CropKind.Wheat, // Futterweizen gemäss Sortenliste swiss granum
        [508] = CropKind.Maize, // Körnermais
        [509] = CropKind.OtherArable, // Reis
        [510] = CropKind.Wheat, // Hartweizen
        [511] = CropKind.Wheat, // Emmer, Einkorn
        [512] = CropKind.Wheat, // Sommerweizen (ohne Futterweizen der Sortenliste swiss granum)
        [513] = CropKind.Wheat, // Winterweizen (ohne Futterweizen der Sortenliste swiss granum)
        [514] = CropKind.Wheat, // Roggen
        [515] = CropKind.Wheat, // Mischel Brotgetreide
        [516] = CropKind.Wheat, // Dinkel
        [519] = CropKind.Maize, // Saatmais (Vertragsanbau)
        [520] = CropKind.OtherArable, // Trockenreis
        [521] = CropKind.Maize, // Silo- und Grünmais
        [522] = CropKind.SugarBeet, // Zuckerrüben
        [523] = CropKind.SugarBeet, // Futterrüben
        [524] = CropKind.Potato, // Kartoffeln
        [525] = CropKind.Potato, // Pflanzkartoffeln (Vertragsanbau)
        [526] = CropKind.Rapeseed, // Sommerraps zur Speiseölgewinnung
        [527] = CropKind.Rapeseed, // Winterraps zur Speiseölgewinnung
        [528] = CropKind.Legumes, // Soja
        [529] = CropKind.OtherArable, // Nassreis
        [531] = CropKind.Sunflower, // Sonnenblumen zur Speiseölgewinnung
        [534] = CropKind.OtherArable, // Lein
        [535] = CropKind.OtherArable, // Hanf
        [536] = CropKind.Legumes, // Bohnen und Wicken zur Körnergewinnung (z.B. Ackerbohnen)
        [537] = CropKind.Legumes, // Erbsen zur Körnergewinnung (z.B. Eiweisserbsen)
        [538] = CropKind.Legumes, // Lupinen
        [539] = CropKind.OtherArable, // Ölkürbisse
        [540] = CropKind.Legumes, // Kichererbsen
        [541] = CropKind.OtherArable, // Tabak
        [542] = CropKind.OtherArable, // Hirse
        [543] = CropKind.Wheat, // Getreide siliert
        [544] = CropKind.OtherArable, // Leindotter
        [545] = CropKind.Vegetables, // Einjährige Freilandgemüse, ohne Konservengemüse
        [546] = CropKind.Vegetables, // Freiland-Konservengemüse
        [547] = CropKind.Vegetables, // Wurzeln der Treibzichorie
        [548] = CropKind.OtherArable, // Buchweizen
        [549] = CropKind.OtherArable, // Sorghum
        [550] = CropKind.OtherArable, // Übrige Ackerfläche
        [551] = CropKind.None, // Einjährige Beeren (z.B. Erdbeeren)
        [552] = CropKind.OtherArable, // Einjährige nachwachsende Rohstoffe (Kenaf, usw.)
        [553] = CropKind.OtherArable, // Einjährige Gewürz- und Medizinalpflanzen
        [554] = CropKind.OtherArable, // Einjährige gärtnerische Freilandkulturen (Blumen, Rollrasen usw.)
        [555] = CropKind.Fallow, // Ackerschonstreifen
        [556] = CropKind.Fallow, // Buntbrache
        [557] = CropKind.Fallow, // Rotationsbrache
        [559] = CropKind.Fallow, // Saum auf Ackerflächen
        [566] = CropKind.OtherArable, // Mohn
        [567] = CropKind.OtherArable, // Saflor
        [568] = CropKind.Legumes, // Linsen
        [569] = CropKind.Legumes, // Mischungen von Bohnen, Wicken, Erbsen, Kichererbsen und Lupinen mit Getreide oder Leindotter, mindestens 30 % Anteil Leguminosen bei der Ernte (zur Körnergewinnung)
        [570] = CropKind.Legumes, // Mischungen von Linsen mit Getreide oder Leindotter, mindestens 30 % Anteil Linsen bei der Ernte (zur Körnergewinnung)
        [572] = CropKind.Fallow, // Nützlingsstreifen auf offener Ackerfläche
        [573] = CropKind.OtherArable, // Senf
        [574] = CropKind.OtherArable, // Quinoa
        [575] = CropKind.OtherArable, // Hanf zur Nutzung der Samen
        [576] = CropKind.OtherArable, // Hanf zur Fasernutzung
        [577] = CropKind.OtherArable, // Anderer Hanf
        [578] = CropKind.OtherArable, // Hirse zur Körnergewinnung
        [579] = CropKind.OtherArable, // Hirse zur Nutzung ganze Pflanze
        [580] = CropKind.OtherArable, // Sorghum zur Körnergewinnung
        [581] = CropKind.OtherArable, // Sorghum zur Nutzung ganze Pflanze
        [590] = CropKind.Rapeseed, // Sommerraps als nachwachsender Rohstoff
        [591] = CropKind.Rapeseed, // Winterraps als nachwachsender Rohstoff
        [592] = CropKind.Sunflower, // Sonnenblumen als nachwachsender Rohstoff
        [594] = CropKind.Fallow, // Offene Ackerfläche, beitragsberechtigt (regionsspezifische Biodiversitätsförderfläche)
        [595] = CropKind.Fallow, // Übrige offene Ackerfläche, nicht beitragsberechtigt (regionsspezifische Biodiversitätsförderfläche)
        [597] = CropKind.OtherArable, // Übrige offene Ackerfläche, beitragsberechtigt
        [598] = CropKind.OtherArable, // Übrige offene Ackerfläche, nicht beitragsberechtigt
        [601] = CropKind.Meadow, // Kunstwiesen (ohne Weiden)
        [602] = CropKind.Pasture, // Übrige Kunstwiese, beitragsberechtigt (z.B. Schweineweide, Geflügelweide)
        [611] = CropKind.Meadow, // Extensiv genutzte Wiesen (ohne Weiden)
        [612] = CropKind.Meadow, // Wenig intensiv genutzte Wiesen (ohne Weiden)
        [613] = CropKind.Meadow, // Übrige Dauerwiesen (ohne Weiden)
        [616] = CropKind.Pasture, // Weiden (Heimweiden, übrige Weiden ohne Sömmerungsweiden)
        [617] = CropKind.Pasture, // Extensiv genutzte Weiden
        [618] = CropKind.Pasture, // Waldweiden (ohne bewaldete Fläche)
        [621] = CropKind.Meadow, // Heuwiesen im Sömmerungsgebiet, Übrige Wiesen
        [622] = CropKind.Meadow, // Heuwiesen im Sömmerungsgebiet, Typ extensiv genutzte Wiese
        [623] = CropKind.Meadow, // Heuwiesen im Sömmerungsgebiet, Typ wenig intensiv genutzte Wiese
        [625] = CropKind.Pasture, // Waldweiden (ohne bewaldete Fläche)
        [631] = CropKind.Meadow, // Futterleguminosen für die Samenproduktion (Vertragsanbau)
        [632] = CropKind.Meadow, // Futtergräser für die Samenproduktion (Vertragsanbau)
        [634] = CropKind.Meadow, // Uferwiesen entlang von Fliessgewässern (ohne Weiden)
        [635] = CropKind.Meadow, // Uferwiesen (ohne Weiden)
        [650] = CropKind.Meadow, // Übrige Dauerwiesen, beitragsberechtigt aggregiert
        [660] = CropKind.Pasture, // Übrige Dauerweiden, beitragsberechtigt aggregiert
        [693] = CropKind.Pasture, // Regionsspezifische Biodiversitätsförderflächen (Weiden)
        [694] = CropKind.Meadow, // Regionsspezifische Biodiversitätsförderfläche (Grünflächen ohne Weiden)
        [697] = CropKind.Meadow, // Übrige Grünfläche (Dauergrünfläche), beitragsberechtigt
        [698] = CropKind.Meadow, // Übrige Grünfläche (Dauergrünflächen), nicht beitragsberechtigt
        [701] = CropKind.None, // Reben
        [702] = CropKind.None, // Obstanlagen (Äpfel)
        [703] = CropKind.None, // Obstanlagen (Birnen)
        [704] = CropKind.None, // Obstanlagen (Steinobst)
        [705] = CropKind.None, // Mehrjährige Beeren
        [706] = CropKind.None, // Mehrjährige Gewürz- und Medizinalpflanzen
        [707] = CropKind.None, // Mehrjährige nachwachsende Rohstoffe (Chinaschilf, usw.)
        [708] = CropKind.None, // Hopfen
        [709] = CropKind.None, // Rhabarber
        [710] = CropKind.None, // Spargel
        [711] = CropKind.None, // Pilze (Freiland)
        [712] = CropKind.None, // Christbäume
        [713] = CropKind.None, // Baumschule von Forstpflanzen ausserhalb der Forstzone
        [714] = CropKind.None, // Ziersträucher, Ziergehölze und Zierstauden
        [715] = CropKind.None, // Übrige Baumschulen (Rosen, Früchte, usw.)
        [717] = CropKind.None, // Rebflächen mit natürlicher Artenvielfalt
        [718] = CropKind.None, // Trüffelanlagen
        [719] = CropKind.None, // Maulbeerbaumanlagen (Fütterung Seidenraupen)
        [720] = CropKind.None, // Gepflegte Selven (Edelkastanienbäume)
        [721] = CropKind.None, // Mehrjährige gärtnerische Freilandkulturen (nicht im Gewächshaus)
        [722] = CropKind.None, // Baumschulen von Reben
        [723] = CropKind.None, // Baumschulen von Obst und Beeren
        [724] = CropKind.None, // Übrige Baumschulen (Rosen, Zierstauden, usw.)
        [725] = CropKind.None, // Permakultur
        [730] = CropKind.None, // Obstanlagen aggregiert
        [731] = CropKind.None, // Andere Obstanlagen (Kiwis, Holunder usw.)
        [735] = CropKind.None, // Reben (regionsspezifische Biodiversitätsförderflächen)
        [750] = CropKind.None, // Übrige Dauerkulturen, beitragsberechtigt, aggregiert
        [760] = CropKind.None, // Dauerkulturen, nicht beitragsberechtigt, aggregiert
        [797] = CropKind.None, // Übrige Flächen mit Dauerkulturen, beitragsberechtigt
        [798] = CropKind.None, // Übrige Flächen mit Dauerkulturen, nicht beitragsberechtigt
        [801] = CropKind.None, // Gemüsekulturen in Gewächshäusern mit festem Fundament
        [802] = CropKind.None, // Übrige Spezialkulturen in Gewächshäusern mit festem Fundament
        [803] = CropKind.None, // Gärtnerische Kulturen in Gewächshäusern mit festem Fundament
        [804] = CropKind.None, // Beerenkulturen in Gewächshäusern mit festem Fundament
        [806] = CropKind.None, // Gemüsekulturen in geschütztem Anbau ohne festes Fundament
        [807] = CropKind.None, // Übrige Spezialkulturen in geschütztem Anbau ohne festes Fundament
        [808] = CropKind.None, // Gärtnerische Kulturen in geschütztem Anbau ohne festes Fundament
        [810] = CropKind.None, // Pilze in geschütztem Anbau mit festem Fundament
        [811] = CropKind.None, // Gemüsekulturen in geschütztem Anbau ohne festes Fundament; im gewachsenen Boden
        [812] = CropKind.None, // Gemüsekulturen in geschütztem Anbau ohne festes Fundament; auf Pflanztischen oder -gestellen
        [813] = CropKind.None, // Beerenkulturen in geschütztem Anbau ohne festes Fundament; im gewachsenen Boden
        [814] = CropKind.None, // Beerenkulturen in geschütztem Anbau ohne festes Fundament; auf Pflanztischen oder -gestellen
        [830] = CropKind.None, // Kulturen in ganzjährig geschütztem Anbau, beitragsberechtigt aggregiert
        [840] = CropKind.None, // Kulturen in ganzjährig geschütztem Anbau, nicht beitragsberechtigt aggregiert
        [847] = CropKind.None, // Übrige Kulturen in geschütztem Anbau ohne festes Fundament, beitragsberechtigt
        [848] = CropKind.None, // Übrige Kulturen in geschütztem Anbau mit festem Fundament
        [849] = CropKind.None, // Übrige Kulturen in geschütztem Anbau ohne festes Fundament, nicht beitragsberechtigt
        [851] = CropKind.None, // Streueflächen in der landwirtschaftlichen Nutzfläche
        [852] = CropKind.None, // Hecken-, Feld- und Ufergehölze (mit Krautsaum)
        [857] = CropKind.None, // Hecken-, Feld- und Ufergehölze (mit Pufferstreifen)
        [858] = CropKind.None, // Hecken-, Feld- und Ufergehölze (mit Pufferstreifen) (regionsspezifische Biodiversitätsförderfläche)
        [897] = CropKind.None, // Übrige Flächen innerhalb der landwirtschaftlichen Nutzfläche, beitragsberechtigt
        [898] = CropKind.None, // Übrige Flächen innerhalb der landwirtschaftlichen Nutzfläche, nicht beitragsberechtigt
        [901] = CropKind.None, // Wald
        [902] = CropKind.None, // Übrige unproduktive Flächen (z.B. gemulchte Flächen, stark verunkrautete Flächen, Hecken ohne Pufferstreifen)
        [903] = CropKind.None, // Flächen ohne landwirtschaftliche Hauptzweckbestimmung (erschlossenes Bauland, Spiel-, Reit-, Camping-, Golf-, Flug- und Militärplätze oder ausgemarchte Bereiche von Eisenbahnen, öffentlichen Strassen und Gewässern)
        [904] = CropKind.None, // Wassergräben, Tümpel, Teiche
        [905] = CropKind.None, // Ruderalflächen, Steinhaufen und -wälle
        [906] = CropKind.None, // Trockenmauern
        [907] = CropKind.None, // Unbefestigte, natürliche Wege
        [908] = CropKind.None, // Regionsspezifische Biodiversitätsförderflächen
        [909] = CropKind.None, // Hausgärten
        [911] = CropKind.None, // Landwirtschaftliche Produktion in Gebäuden (z. B. Champignon, Brüsseler)
        [921] = CropKind.None, // Hochstamm-Feldobstbäume (Punkte oder Flächen)
        [922] = CropKind.None, // Nussbäume (Punkte oder Flächen)
        [923] = CropKind.None, // Edelkastanienbäume
        [924] = CropKind.None, // Einheimische standortgerechte Einzelbäume und Alleen (Punkte oder Flächen)
        [926] = CropKind.None, // Andere Bäume
        [927] = CropKind.None, // Andere Bäume (regionsspezifische Biodiversitätsförderfläche)
        [928] = CropKind.None, // Andere Elemente (regionsspezifische Biodiversitätsförderfläche)
        [930] = CropKind.Pasture, // Sömmerungsweiden
        [933] = CropKind.Pasture, // Gemeinschaftsweiden
        [935] = CropKind.Meadow, // Heuwiesen mit Zufütterung während der Sömmerung
        [936] = CropKind.None, // Streueflächen im Sömmerungsgebiet
        [950] = CropKind.None, // Ackerschonstreifen
        [951] = CropKind.None, // Getreide in weiter Reihe
        [998] = CropKind.None, // Übrige Flächen ausserhalb der landwirtschaftlichen Nutzfläche und Sömmerungsfläche
    };

    /// <summary>The crop of a code: null when the table has never heard of it.</summary>
    public static CropKind? Of(int lnfCode) => Table.TryGetValue(lnfCode, out var k) ? k : null;
}
