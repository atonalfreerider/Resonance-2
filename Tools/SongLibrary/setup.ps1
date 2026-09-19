param([string]$Python = 'py', [switch]$Cpu)
$ErrorActionPreference = 'Stop'
$songRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$prepEnv = Join-Path $songRoot 'Tools/SongPrep/.venv'
$modelEnv = Join-Path $PSScriptRoot '.venv'
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw 'Setup command failed. See output above.' } }
foreach ($songEnv in @($prepEnv, $modelEnv)) {
    if (!(Test-Path -LiteralPath (Join-Path $songEnv 'Scripts/python.exe'))) {
        & $Python -m venv $songEnv; Check-Exit
    }
}
$prepPython = Join-Path $prepEnv 'Scripts/python.exe'
$modelPython = Join-Path $modelEnv 'Scripts/python.exe'
& $prepPython -m pip install -r (Join-Path $songRoot 'Tools/SongPrep/requirements.txt') librosa==0.11.0 pretty-midi==0.2.11 requests==2.32.5; Check-Exit
$wheelIndex = if ($Cpu) { 'https://download.pytorch.org/whl/cpu' } else { 'https://download.pytorch.org/whl/cu126' }
& $modelPython -m pip install torch==2.7.1 torchvision==0.22.1 torchaudio==2.7.1 --index-url $wheelIndex; Check-Exit
& $modelPython -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt'); Check-Exit
& $prepPython (Join-Path $PSScriptRoot 'transcribe.py') --install; Check-Exit
& $prepPython (Join-Path $PSScriptRoot 'library.py') doctor; Check-Exit
