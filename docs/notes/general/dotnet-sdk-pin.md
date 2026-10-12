# .NET SDK pin (`global.json`)

`global.json` pins the build to the **.NET 9 SDK** (`9.0.100`, `rollForward: latestFeature`: any 9.0.x, never 10).
Projects still target `net8.0`; the 9 SDK builds them and is also what Godot's Android template needs.

Why: GitHub runners ship the newest SDK preinstalled, and without a pin `dotnet` picks it. The .NET 10 SDK
compiles `LangVersion latest` as C# 14, where `array.Reverse()` binds to the in-place
`MemoryExtensions.Reverse(Span<T>)` (returns `void`) instead of LINQ: that broke the release build on CI
while every local machine (SDK 9) built fine. On arrays, write `arr.AsEnumerable().Reverse()`.

Needs the 9 SDK installed locally (`dotnet --list-sdks`); with only 8 or only 10, `dotnet build` stops with
"A compatible .NET SDK was not found". Moving to SDK 10: bump `global.json` and the `setup-dotnet`
versions in `.github/workflows/release.yml` together, then fix what C# 14 breaks.
