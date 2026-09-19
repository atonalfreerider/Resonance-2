$ErrorActionPreference = 'Stop'
$songRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$songPython = Join-Path $songRoot 'Tools/SongPrep/.venv/Scripts/python.exe'
if (!(Test-Path -LiteralPath $songPython)) { throw 'Run Tools/SongLibrary/setup.ps1 first.' }
if ($args.Count -eq 0) { & $songPython (Join-Path $PSScriptRoot 'library.py') serve }
else { & $songPython (Join-Path $PSScriptRoot 'library.py') @args }
exit $LASTEXITCODE
