param(
    [Parameter(Mandatory=$true)][string]$Audio,
    [Parameter(Mandatory=$true)][string]$Midi,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Python = 'python',
    [string]$Anchors = ''
)
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $runtime)) {
    & $Python -m venv (Join-Path $PSScriptRoot '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.11 or 3.12 is required.' }
    & $runtime -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not install preprocessing dependencies.' }
}
$arguments = @((Join-Path $PSScriptRoot 'prepare_song.py'), '--audio', $Audio, '--midi', $Midi, '--output', $Output)
if ($Anchors) { $arguments += @('--anchors', $Anchors) }
& $runtime @arguments
if ($LASTEXITCODE -ne 0) { throw 'Preprocessing failed. Do not load unfinished outputs.' }
Write-Host "Prepared MIDI: $(Join-Path $Output 'aligned.mid')"
Write-Host "Review before Unity: $(Join-Path $Output 'report.html')"
