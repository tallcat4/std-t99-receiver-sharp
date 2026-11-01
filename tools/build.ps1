# Full build: SDR# references (from your local SDR#, never redistributed), native DLL, test fixture, plugin.
param([string]$SdrSharpExe,[string]$Compiler='g++')
$ErrorActionPreference='Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    New-Item -ItemType Directory -Force build/sdk | Out-Null
    if($SdrSharpExe) { & python tools/extract-sdk.py $SdrSharpExe --output build/sdk; if($LASTEXITCODE) {throw 'SDK extraction failed'} }
    if(!(Test-Path build/sdk/SDRSharp.Common.dll)) {throw 'Supply -SdrSharpExe with your SDR# x64 executable (SDRSharp.dotnet8.exe)'}
    & ./tools/build-native.ps1 -Compiler $Compiler
    $includes=@('-Itests','-Ibuild/fftw','-Ithird_party/std_t99_receiver/include','-Ithird_party/spear/apps/std_t98','-Ithird_party/spear/dsp/include')
    & $Compiler -std=c++20 -O2 -static @includes tests/spec_iq.cpp third_party/spear/apps/std_t98/dsp/rrc.cpp third_party/spear/dsp/src/fftw_planner.cpp build/fftw/libfftw3f-3.dll -o build/spec-iq.exe
    if($LASTEXITCODE) {throw 'Test signal generator build failed'}
    Copy-Item build/fftw/libfftw3f-3.dll build/
    & dotnet build src/SDRSharp.T99 -c Release --nologo -p:NuGetAudit=false -p:UseSharedCompilation=false
    if($LASTEXITCODE) {throw 'Plugin build failed'}
} finally {Pop-Location}
