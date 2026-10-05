# Godot executable

- WSL / Linux: just `godot`.
- macOS: the binary inside the app bundle, `/Applications/Godot_mono.app/Contents/MacOS/Godot`, not
  the `.app` itself. A `godot` on `PATH` (a link under `~/.local/bin`, say) works the same. Give
  `tools/test.sh` the binary: `GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot tools/test.sh quick`.
- Windows, Chocolatey (`choco install godot-mono --version 4.7.1`): `C:\ProgramData\chocolatey\lib\godot-mono\tools\godot_v4.7.1-stable_mono_win64\godot_v4.7.1-stable_mono_win64_console.exe`
- Windows, winget (`winget install GodotEngine.GodotEngine.Mono --version 4.7.1`):
  `%LOCALAPPDATA%\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe`.
  Call that exe directly: the winget `godot_console` link in `WinGet\Links` starts the GUI build and
  hangs a headless `--import`.
