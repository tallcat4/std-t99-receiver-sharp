# Release folder: STD-T99-Receiver.zip (plugin + documents) and SHA256SUMS.txt
param([string]$Output)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if(!$Output) {$Output=Join-Path $projectRoot ('artifacts/release-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
    if(Test-Path -LiteralPath $Output) {throw 'Package destination already exists'}
    if(!(Test-Path build/native/t99_native.dll)) {throw 'Build the native receiver first'}
    [xml]$project=Get-Content -LiteralPath src/SDRSharp.T99/SDRSharp.T99.csproj
    $version=[string]$project.Project.PropertyGroup.Version
    $assembly='src/SDRSharp.T99/bin/Release/net8.0-windows/SDRSharp.Plugin.T99.dll'
    $built=[Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $assembly).Path).ProductVersion.Split('+')[0]
    if($built -ne $version) {throw "Rebuild the plugin: DLL version $built does not match $version"}
    $plugin=Join-Path $Output 'Plugins/SDRSharp.T99'
    New-Item -ItemType Directory -Force $plugin | Out-Null
    Copy-Item $assembly $plugin
    foreach($name in @('t99_native.dll','libfftw3f-3.dll')) {Copy-Item (Join-Path 'build/native' $name) $plugin}
    Copy-Item -Recurse licenses (Join-Path $Output 'licenses')
    Copy-Item README.md,LICENSE,FORMAT.md,PROVENANCE.md,THIRD_PARTY_NOTICES.md,thumbnail.png $Output
    Set-Content -LiteralPath (Join-Path $Output 'VERSION.txt') -Value $version -Encoding ascii
    # Zip entries use '/' (the zip format's separator) on every PowerShell version; Compress-Archive of
    # Windows PowerShell 5.1 would write '\', which some extractors keep as part of the file name.
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $root=(Resolve-Path -LiteralPath $Output).Path.TrimEnd('\','/')
    $zip=[IO.Compression.ZipFile]::Open((Join-Path $root 'STD-T99-Receiver.zip'),[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($name in @('Plugins','licenses','README.md','LICENSE','FORMAT.md','PROVENANCE.md','THIRD_PARTY_NOTICES.md','VERSION.txt','thumbnail.png')) {
            $item=Get-Item -LiteralPath (Join-Path $root $name)
            $files=if($item.PSIsContainer) {Get-ChildItem -LiteralPath $item.FullName -Recurse -File | Sort-Object FullName} else {@($item)}
            foreach($file in $files) {
                $entry=$file.FullName.Substring($root.Length+1).Replace('\','/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal)
            }
        }
    } finally {$zip.Dispose()}
    $checksums=@()
    $hash=(Get-FileHash -LiteralPath (Join-Path $Output 'STD-T99-Receiver.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums+="$hash  STD-T99-Receiver.zip"
    foreach($file in Get-ChildItem -LiteralPath $plugin -File | Sort-Object Name) {
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksums+="$hash  Plugins/SDRSharp.T99/$($file.Name)"
    }
    Set-Content -LiteralPath (Join-Path $Output 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
    Write-Host "Ready: $Output"
} finally {Pop-Location}
