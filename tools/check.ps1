# Native receiver chain on the Annex 2 test signals, decoder equivalence with PyTorch, SDR# host simulation.
param([string]$Neurodec)
$ErrorActionPreference='Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    & python tools/synthetic-check.py
    if($LASTEXITCODE) {throw 'Native receiver check failed'}
    # Needs PyTorch and a tokuder-neurodec checkout (default: ../references/tokuder-neurodec)
    if(!$Neurodec) {$Neurodec=Join-Path (Split-Path -Parent (Get-Location)) 'references/tokuder-neurodec'}
    & python -c 'import torch' 2>$null
    if((Test-Path $Neurodec) -and $LASTEXITCODE -eq 0) {
        & python tools/check-native-model.py --neurodec $Neurodec
        if($LASTEXITCODE) {throw 'Native TOKUDER decoder differs from the PyTorch reference'}
    } else {Write-Warning 'Skipped the decoder equivalence check (PyTorch or tokuder-neurodec not found)'}
    & ./build/spec-iq.exe build/spec-48000.cf32 48000 180
    if($LASTEXITCODE) {throw 'Test signal generation failed'}
    & dotnet build src/T99.Smoke -c Release --nologo -p:NuGetAudit=false -p:UseSharedCompilation=false
    if($LASTEXITCODE) {throw 'Smoke build failed'}
    Copy-Item build/native/*.dll src/T99.Smoke/bin/Release/net8.0-windows/
    & src/T99.Smoke/bin/Release/net8.0-windows/T99.Smoke.exe .
    if($LASTEXITCODE) {throw 'SDR# host simulation failed'}
} finally {Pop-Location}
