# Check a worktree by hand: build it and start Godot from it, wired to the main checkout's terrain.
# Usage: tools/wt.ps1 [<tree>] [<mode>] [-Port N] [-Clean] [-NoBuild] [-Scratch] [-- game args]
#   <tree>  issue number (446), list number (17), branch or path fragment (turboprop); omitted = pick from a list;
#           `list` = only print every worktree with its PR (number, state, title) and exit
#   <mode>  play    one windowed client, offline (default)
#           net     headless server + one windowed client connected to it
#           two     headless server + two windowed clients (A and B): check the remote peer
#           server  headless server only, in this console (Ctrl+C stops it)
#           editor  the Godot editor on that worktree (what the godot-ai MCP then talks to)
#   -Clean    dotnet build --no-incremental: after copying .godot/ from another tree, the old assembly stays
#   -NoBuild  skip the build
#   -Scratch  user:// under <tree>/test_output/userdata_wt instead of the real settings, saves and inventory
#   -- ...    passed to every game process, e.g. -- --at 2600000,1200000
# Worktrees have no terrain_chunks/ (gitignored): without a manifest.json there, --chunks points at the
# main checkout's. Servers pick a free UDP port from 7800 and are killed when the clients close.
# Examples:  tools/wt.ps1 446 two      tools/wt.ps1 turboprop net -Scratch      tools/wt.ps1 469 editor
param(
    [Parameter(Position = 0)][string]$Tree,
    [Parameter(Position = 1)][ValidateSet('play', 'net', 'two', 'server', 'editor')][string]$Mode = 'play',
    [int]$Port = 0,
    [switch]$Clean,
    [switch]$NoBuild,
    [switch]$Scratch,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$GameArgs = @()
)
$ErrorActionPreference = "Stop"
$GameArgs = @($GameArgs | Where-Object { $_ -ne '--' })

# git worktree list --porcelain: "worktree <path>" then "branch refs/heads/<name>" (or "detached"); the first is the main checkout
$trees = @()
foreach ($line in (git -C $PSScriptRoot worktree list --porcelain)) {
    if ($line -like "worktree *") { $trees += [pscustomobject]@{ Path = $line.Substring(9); Branch = "(detached)" } }
    elseif ($line -like "branch refs/heads/*") { $trees[-1].Branch = $line.Substring(18) }
}
$main = $trees[0].Path

# What each tree is for: its PR (title, number, state) from gh, else the branch's last commit subject
function Show-Trees {
    $prs = @{}
    try {
        $json = gh pr list --repo SuperQuentin/UnitSportSwitzerland --state all --limit 300 --json headRefName,number,title,state,isDraft 2>$null
        foreach ($p in ($json | ConvertFrom-Json)) { if (-not $prs.ContainsKey($p.headRefName)) { $prs[$p.headRefName] = $p } }
    } catch { Write-Host "(gh unavailable: no PR titles)" }
    for ($i = 0; $i -lt $trees.Count; $i++) {
        $tr = $trees[$i]; $p = $prs[$tr.Branch]
        if ($p) {
            $state = if ($p.isDraft -and $p.state -eq 'OPEN') { 'DRAFT' } else { $p.state }
            $about = "#$($p.number) $state  $($p.title)"
        } else {
            $about = "no PR   " + (git -C $tr.Path log -1 --format=%s 2>$null)
        }
        $dirty = if (git -C $tr.Path status --porcelain 2>$null | Select-Object -First 1) { '*' } else { ' ' }
        Write-Host ("  [{0,2}]{1}{2,-34} {3}" -f ($i + 1), $dirty, $tr.Branch, $about)
    }
    Write-Host "  (* = uncommitted changes)"
}
if ($Tree -eq 'list') { Show-Trees; exit 0 }

if ($Tree) {
    $hits = @(if ($Tree -match '^\d+$') {
            $trees | Where-Object { $_.Path -match "-$Tree$" -or $_.Branch -match "^[^/]+/$Tree-" }
        } elseif (Test-Path $Tree) {
            $full = (Resolve-Path $Tree).Path.TrimEnd('\') -replace '\\', '/'
            $trees | Where-Object { $_.Path -eq $full }
        } else {
            $trees | Where-Object { $_.Branch -like "*$Tree*" -or $_.Path -like "*$Tree*" }
        })
    # no issue matches a small number: take it as the [n] of `tools/wt.ps1 list`
    if ($hits.Count -eq 0 -and $Tree -match '^\d+$' -and [int]$Tree -ge 1 -and [int]$Tree -le $trees.Count) { $hits = @($trees[[int]$Tree - 1]) }
    if ($hits.Count -ne 1) {
        Write-Host "'$Tree' matches $($hits.Count) worktrees:"; $hits | ForEach-Object { Write-Host "  $($_.Branch)  $($_.Path)" }; exit 1
    }
    $t = $hits[0]
} else {
    Show-Trees
    $pick = Read-Host "Worktree number"
    $n = 0
    if (-not [int]::TryParse($pick, [ref]$n) -or $n -lt 1 -or $n -gt $trees.Count) { Write-Host "No such worktree: $pick"; exit 1 }
    $t = $trees[$n - 1]
}
$path = $t.Path
Write-Host "== $($t.Branch)  ($path)  mode $Mode"

# Godot: $env:GODOT, else the console exe from winget or Chocolatey (docs/notes/general/godot-exe.md), else godot on PATH
$godot = $env:GODOT
if (-not $godot) {
    $cands = @(Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_*\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe" -ErrorAction SilentlyContinue).FullName +
        "C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe"
    $godot = $cands | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $godot) { $godot = (Get-Command godot -ErrorAction Stop).Source }
}

if (-not $NoBuild) {
    $build = @((Join-Path $path "UnitSportSwitzerland.csproj"), "-nologo", "-v", "q", "/clp:NoSummary")
    if ($Clean) { $build += "--no-incremental" }
    dotnet build @build
    if ($LASTEXITCODE -ne 0) { Write-Host "Build failed"; exit $LASTEXITCODE }
}
# A fresh worktree has no .godot/ yet: import once, headless, before the first run
if (-not (Test-Path (Join-Path $path ".godot"))) { & $godot --headless --path $path --import | Out-Null }

$ch = @()
if (-not (Test-Path (Join-Path $path "terrain_chunks/manifest.json"))) { $ch = @("--chunks", "$main/terrain_chunks") }
$out = Join-Path $path "test_output"
New-Item -ItemType Directory -Force $out | Out-Null
if ($Scratch) {
    $env:APPDATA = Join-Path $out "userdata_wt"
    New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
    Write-Host "user:// under $env:APPDATA"
}

if ($Mode -eq 'editor') {
    Start-Process $godot -ArgumentList (@("--editor", "--path", "`"$path`"")) | Out-Null
    exit 0
}

function Start-Game([string[]]$a) {
    Start-Process $godot -PassThru -ArgumentList (@("--path", "`"$path`"", "--") + $a + $ch + $GameArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
}

if ($Mode -eq 'play') { Start-Game @() | Out-Null; exit 0 }

if ($Port -eq 0) {
    $Port = 7800
    while (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue) { $Port++ }
}
$serverArgs = @("--server", "--port", "$Port") + $ch + $GameArgs
if ($Mode -eq 'server') { & $godot --headless --path $path -- @serverArgs; exit $LASTEXITCODE }

$log = Join-Path $out "wt-server.log"
$server = Start-Process $godot -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "wt-server.err.log") `
    -ArgumentList (@("--headless", "--path", "`"$path`"", "--") + ($serverArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }))
try {
    # A full-world server blends its horizon for ~30 s before it listens: connecting earlier fails
    Write-Host "Server pid $($server.Id) on UDP $Port, log $log; waiting for it to listen..."
    $deadline = (Get-Date).AddSeconds(180)
    while (-not (Select-String -Path $log -Pattern "server listening" -Quiet -ErrorAction SilentlyContinue)) {
        if ($server.HasExited) { Write-Host "Server died (exit $($server.ExitCode)), see $log"; exit 1 }
        if ((Get-Date) -gt $deadline) { Write-Host "Server not listening after 180 s, see $log"; exit 1 }
        Start-Sleep -Milliseconds 500
    }
    $clients = @(Start-Game @("--connect", "127.0.0.1:$Port", "--name", "A"))
    if ($Mode -eq 'two') { $clients += Start-Game @("--connect", "127.0.0.1:$Port", "--name", "B") }
    Write-Host "Clients up; close them (or Ctrl+C) to stop the server."
    $clients | Wait-Process
} finally {
    if (-not $server.HasExited) { taskkill /T /F /PID $server.Id | Out-Null }
    Write-Host "Server stopped."
}
