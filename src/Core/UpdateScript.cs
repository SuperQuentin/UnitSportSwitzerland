namespace UnitSport.Core;

/// <summary>
/// The script that finishes a delta update (#532). A running game cannot overwrite its own
/// executable or .pck on Windows, so the game stages the new files (<see cref="UpdatePackage.Staging"/>),
/// starts this script and quits: it waits for the game's process to end, moves
/// <c>.update/files/*</c> over the install, deletes what <c>.update/delete.txt</c> lists, removes
/// <c>.update</c> and, if asked, starts the game again. PowerShell on Windows, sh elsewhere. A
/// failure is written to <c>update-error.txt</c> in the install.
/// </summary>
public static class UpdateScript
{
    public static string FileName(bool windows) => windows ? "apply.ps1" : "apply.sh";

    /// <summary>The program and arguments that run <paramref name="script"/> detached and hidden.</summary>
    public static (string Program, string[] Args) Command(bool windows, string script) => windows
        ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
        : ("/bin/sh", new[] { script });

    public static string Text(bool windows, int pid, string root, string? relaunch) =>
        windows ? Windows(pid, root, relaunch) : Unix(pid, root, relaunch);

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Windows(int pid, string root, string? relaunch) => $$"""
        $ErrorActionPreference = 'Stop'
        $root = {{Ps(root)}}
        $dir = Join-Path $root '.update'
        try {
          Wait-Process -Id {{pid}} -ErrorAction SilentlyContinue
          $files = (Resolve-Path -LiteralPath (Join-Path $dir 'files')).ProviderPath
          Get-ChildItem -LiteralPath $files -Recurse -File | ForEach-Object {
            $dst = Join-Path $root $_.FullName.Substring($files.Length + 1)
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
            for ($i = 0; ; $i++) {
              try { Move-Item -LiteralPath $_.FullName -Destination $dst -Force; break }
              catch { if ($i -ge 40) { throw }; Start-Sleep -Milliseconds 500 }
            }
          }
          Get-Content -LiteralPath (Join-Path $dir 'delete.txt') | Where-Object { $_ } | ForEach-Object {
            Remove-Item -LiteralPath (Join-Path $root $_) -Force -ErrorAction SilentlyContinue
          }
          Remove-Item -LiteralPath $dir -Recurse -Force
        } catch {
          "$_" | Out-File -FilePath (Join-Path $root 'update-error.txt')
          exit 1
        }
        {{(relaunch != null ? $"Start-Process -FilePath {Ps(relaunch)} -WorkingDirectory $root" : "")}}

        """;

    private static string Unix(int pid, string root, string? relaunch) => $$"""
        #!/bin/sh
        ROOT={{Sh(root)}}
        DIR="$ROOT/.update"
        fail() { echo "$1" > "$ROOT/update-error.txt"; exit 1; }
        while kill -0 {{pid}} 2>/dev/null; do sleep 0.5; done
        cd "$DIR/files" || fail "no staged files"
        find . -type f > "$DIR/list.txt" || fail "cannot list the staged files"
        while IFS= read -r f; do
          mkdir -p "$ROOT/$(dirname "$f")" && mv -f "$f" "$ROOT/$f" || fail "cannot move $f"
        done < "$DIR/list.txt"
        while IFS= read -r f; do
          [ -n "$f" ] && rm -f "$ROOT/$f"
        done < "$DIR/delete.txt"
        cd "$ROOT" && rm -rf "$DIR"
        {{(relaunch != null ? $"{Sh(relaunch)} >/dev/null 2>&1 &" : "")}}

        """;
}
