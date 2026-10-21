# VR without a headset: the game in VR mode against Meta XR Simulator, a simulated Quest whose
# head and controllers move by mouse, keyboard or gamepad (docs/notes/xr/simulator.md).
# Usage: tools/vr-sim.ps1 <godot.exe> [game args...]   e.g. tools/vr-sim.ps1 $godot --chunks C:/.../terrain_chunks
# Only this game process uses the simulator (XR_RUNTIME_JSON): the system's OpenXR runtime (Meta
# Horizon Link) stays the active one for everything else. Set XRSIM_JSON to use a given install.
# Plain $args, no param(): a named parameter would choke on game args that start with "--".
$ErrorActionPreference = "Stop"
$Godot = $args[0]
if (-not $Godot) { Write-Host "Usage: vr-sim.ps1 <godot.exe> [game args...]"; exit 1 }
$GameArgs = @($args | Select-Object -Skip 1)

# the newest simulator install: the standalone app or MQDH's copy
$json = $env:XRSIM_JSON
if (-not $json) {
    $roots = @("$env:LOCALAPPDATA\MetaXR\MetaXrSimulator", "$env:ProgramFiles\Meta XR Simulator", "$env:ProgramFiles\Meta\Meta XR Simulator")
    $found = foreach ($r in $roots) {
        if (Test-Path $r) { Get-ChildItem -Path $r -Filter "meta_openxr_simulator.json" -Recurse -ErrorAction SilentlyContinue }
    }
    $json = ($found | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $json -or -not (Test-Path $json)) {
    Write-Host "Meta XR Simulator not found. Install it (Meta Quest Developer Hub, or developers.meta.com 'Meta XR Simulator'),"
    Write-Host "or set XRSIM_JSON to its meta_openxr_simulator.json."
    exit 1
}
Write-Host "OpenXR runtime for this run: $json"

$env:XR_RUNTIME_JSON = $json
$project = Split-Path $PSScriptRoot -Parent
& $Godot --xr-mode on --rendering-driver vulkan --path $project -- --vr @GameArgs
exit $LASTEXITCODE
