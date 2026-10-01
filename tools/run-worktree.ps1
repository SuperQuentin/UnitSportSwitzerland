# Pick a git worktree (a local feature branch), build it and run the game from it.
# Usage: tools/run-worktree.ps1 <godot.exe> ["--connect 127.0.0.1"]
# Run by the VS Code task "run: game from worktree"; tools/run-worktree.sh is the macOS/Linux twin.
# Plain $args, no param(): a named parameter would choke on game args that start with "--".
$ErrorActionPreference = "Stop"
$Godot = $args[0]
$GameArgs = if ($args.Count -gt 1) { "$($args[1])" } else { "" }
if (-not $Godot) { Write-Host "Usage: run-worktree.ps1 <godot.exe> [game args]"; exit 1 }

# git worktree list --porcelain: "worktree <path>" then "branch refs/heads/<name>" (or "detached")
$trees = @()
foreach ($line in (git -C $PSScriptRoot worktree list --porcelain)) {
    if ($line -like "worktree *") { $trees += [pscustomobject]@{ Path = $line.Substring(9); Branch = "(detached)" } }
    elseif ($line -like "branch refs/heads/*") { $trees[-1].Branch = $line.Substring(18) }
}

for ($i = 0; $i -lt $trees.Count; $i++) {
    Write-Host ("  [{0}] {1,-32} {2}" -f ($i + 1), $trees[$i].Branch, $trees[$i].Path)
}
$pick = Read-Host "Worktree number"
$n = 0
if (-not [int]::TryParse($pick, [ref]$n) -or $n -lt 1 -or $n -gt $trees.Count) { Write-Host "No such worktree: $pick"; exit 1 }
$tree = $trees[$n - 1]
Write-Host "Running $($tree.Branch) from $($tree.Path)"

dotnet build (Join-Path $tree.Path "UnitSportSwitzerland.csproj") /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# A fresh worktree has no .godot/ yet: import once, headless, before the first run
if (-not (Test-Path (Join-Path $tree.Path ".godot"))) {
    & $Godot --headless --path $tree.Path --import
}

$extra = @($GameArgs -split '\s+' | Where-Object { $_ })
if ($extra.Count -gt 0) { & $Godot --path $tree.Path -- @extra } else { & $Godot --path $tree.Path }
exit $LASTEXITCODE
