# Native receiver + TOKUDER decoder: build/native/t99_native.dll (+ libfftw3f-3.dll)
param([string]$Compiler='g++')
$ErrorActionPreference='Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    New-Item -ItemType Directory -Force build/native | Out-Null
    if(!(Test-Path build/fftw/fftw3.h)) { & ./tools/setup-fftw.ps1 }
    # The voice model is embedded in the DLL (no separate model file to lose or mismatch)
    $model=(Resolve-Path models/tokuder48_causal_v1.bin).Path.Replace('\','/')
    Set-Content -LiteralPath build/native/model.S -Encoding ascii -Value @(
        '.section .rdata,"dr"','.balign 16','.globl t99_model_start','.globl t99_model_end',
        't99_model_start:',".incbin `"$model`"",'t99_model_end:')
    & gcc -c build/native/model.S -o build/native/model.o
    if($LASTEXITCODE) {throw 'Model assembly failed'}
    $includes=@('-Inative','-Ithird_party/std_t99_receiver','-Ithird_party/std_t99_receiver/include','-Ithird_party/spear/apps/std_t98','-Ithird_party/spear/dsp/include','-Ibuild/fftw')
    # The decoder's dot products vectorize only with reassociation; it is self-contained float code.
    & $Compiler -std=c++20 -O3 -ffast-math -c native/tokuder.cpp @includes -o build/native/tokuder.o
    if($LASTEXITCODE) {throw 'Decoder build failed'}
    $sources=@('native/t99.cpp') + (@('protocol','messages','session','receiver','frame_sync') | ForEach-Object {"third_party/std_t99_receiver/$_.cpp"}) +
        @('third_party/spear/apps/std_t98/dsp/rrc.cpp','third_party/spear/apps/std_t98/dsp/symbol_sync.cpp','third_party/spear/dsp/src/fir.cpp','third_party/spear/dsp/src/fftw_planner.cpp')
    & $Compiler -std=c++20 -O3 -shared -static @includes @sources build/native/tokuder.o build/native/model.o build/fftw/libfftw3f-3.dll -o build/native/t99_native.dll
    if($LASTEXITCODE) {throw 'Receiver build failed'}
    Copy-Item build/fftw/libfftw3f-3.dll build/native/
    Write-Host 'Built build/native/t99_native.dll'
} finally {Pop-Location}
