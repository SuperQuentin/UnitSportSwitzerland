namespace UnitSport.Core;

/// <summary>
/// Every data source, engine and bundled component the game or its build uses, as shown on
/// Settings > Licenses. Add a source here and the page picks it up. Each text was checked
/// against the owner's own terms page (docs/notes/core/licenses.md).
/// </summary>
public static class Licenses
{
    public sealed record Entry(string Name, string UsedFor, string Licence, string Url, string Attribution);

    public static readonly Entry[] All =
    [
        new("swisstopo: swissALTI3D, swissTLM3D, swissBUILDINGS3D 3.0, swissBOUNDARIES3D",
            "Terrain heights, roads, railways, land cover, trees, buildings, borders",
            "swisstopo terms of use for free geodata (OGD)",
            "https://www.swisstopo.admin.ch/en/terms-of-use-free-geodata-and-geoservices",
            "© swisstopo (Federal Office of Topography swisstopo)"),
        new("GWR, Federal Register of Buildings and Dwellings",
            "Building use, age and storeys; the place index",
            "opendata.swiss: open use",
            "https://opendata.swiss/en/terms-of-use#terms_open",
            "Source: Federal Statistical Office (FSO), Federal Register of Buildings and Dwellings (GWR)"),
        new("Veloland Schweiz and Mountainbikeland Schweiz",
            "Signed cycle and mountain bike routes on roads",
            "opendata.swiss: open use, must provide the source",
            "https://opendata.swiss/en/terms-of-use#terms_by",
            "Source: Federal Roads Office FEDRO, cantons, SwitzerlandMobility Foundation"),
        new("OpenStreetMap",
            "Optional: one-way streets, lanes, widths, sidewalks and cycleways on roads, when a region is built with the OSM layer. "
            + "Road tiles built with it are a derived database under the ODbL and are provided on request",
            "Open Database License (ODbL) 1.0",
            "https://www.openstreetmap.org/copyright",
            "© OpenStreetMap contributors"),
        new("IGN BD TOPO® (France)",
            "Roads and buildings across the French border",
            "Licence Ouverte / Open Licence 2.0 (Etalab)",
            "https://www.etalab.gouv.fr/wp-content/uploads/2018/11/open-licence.pdf",
            "Source: IGN – BD TOPO®"),
        new("Godot Engine",
            "The game engine, including GodotSharp and the Godot .NET SDK",
            "MIT; its third-party components are listed below",
            "https://godotengine.org/license",
            "This game uses Godot Engine, available under the MIT license. "
            + "Copyright (c) 2014-present Godot Engine contributors. Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur."),
        new("Godot logo (project icon)",
            "Window and application icon",
            "CC BY 4.0",
            "https://creativecommons.org/licenses/by/4.0/",
            "© 2017 Andrea Calabró"),
        new("Godot AI addon",
            "Editor and test automation bridge (addons/godot_ai)",
            "MIT",
            "https://github.com/hi-godot/godot-ai",
            "Copyright (c) 2025 Godot AI contributors"),
        new("SDL3 (bundled)",
            "Steering wheels and pedals, and their force feedback",
            "zlib License",
            "https://github.com/libsdl-org/SDL/blob/main/LICENSE.txt",
            "Copyright (C) 1997-2025 Sam Lantinga"),
        new("SDL3-CS (bundled)",
            "The C# bindings to SDL3",
            "MIT License",
            "https://github.com/ppy/SDL3-CS/blob/master/LICENCE",
            "ppy Pty Ltd"),
        new("yt-dlp (not bundled)",
            "Run by a server, when installed there, to fetch audio for radio CDs; what is fetched is the responsibility of whoever submits the link",
            "The Unlicense",
            "https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE",
            "yt-dlp contributors"),
        new("FFmpeg (not bundled)",
            "Run by a server, when installed there, to convert radio CD audio",
            "LGPL 2.1 or later, or GPL 2 or later, depending on the build installed",
            "https://ffmpeg.org/legal.html",
            "FFmpeg developers"),
    ];
}
