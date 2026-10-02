param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repoRoot
try {
    function Invoke-Dotnet([string[]]$Arguments) {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
    }
    Invoke-Dotnet -Arguments @('clean','M365Collector.sln','-c','Release','--nologo')
    Invoke-Dotnet -Arguments @('restore','M365Collector.sln','--locked-mode')
    Invoke-Dotnet -Arguments @('build','M365Collector.sln','-c','Release','--no-restore','--nologo','-warnaserror')
    if (-not $SkipTests) { Invoke-Dotnet -Arguments @('test','M365Collector.sln','-c','Release','--no-build','--nologo','--logger','trx;LogFileName=release-tests.trx') }
    $stagingPath = Join-Path $repoRoot ('dist\package-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    foreach ($component in @('GUI','Service','Updater')) {
        Invoke-Dotnet -Arguments @('publish',"src\M365Collector.$component\M365Collector.$component.csproj",'-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-p:DebugSymbols=false','-o',(Join-Path $stagingPath $component),'--nologo')
        $componentFiles = @(Get-ChildItem -LiteralPath (Join-Path $stagingPath $component) -File -Recurse)
        if ($componentFiles.Count -ne 1 -or $componentFiles[0].Name -ne "M365Collector.$component.exe") { throw "Component $component must publish as one executable." }
    }
    $manifestFiles = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $stagingPath -File -Recurse | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($stagingPath, $file.FullName).Replace('\','/')
        if ($file.Extension -notin @('.exe','.dll','.json','.config','.xml','.pri','.dat')) { throw "Unexpected release file: $relative" }
        $manifestFiles[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    @{version='0.0.3';files=$manifestFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stagingPath 'package.json') -Encoding utf8NoBOM
    $zipPath = Join-Path $repoRoot 'dist\M365Collector-0.0.3-win-x64.zip'
    # Only the explicitly named generated ZIP is replaced. No recursive deletion.
    if (Test-Path -LiteralPath $zipPath) { [IO.File]::Delete($zipPath) }
    [IO.Compression.ZipFile]::CreateFromDirectory($stagingPath,$zipPath,[IO.Compression.CompressionLevel]::Optimal,$false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii
    $launcherOutput = Join-Path $repoRoot ('dist\launcher-' + [guid]::NewGuid().ToString('N'))
    Invoke-Dotnet -Arguments @('publish','src\M365Collector.Launcher\M365Collector.Launcher.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-p:DebugSymbols=false',"-p:BundleZip=$zipPath",'-o',$launcherOutput,'--nologo')
    if (@(Get-ChildItem -LiteralPath $launcherOutput -File -Recurse).Count -ne 1) { throw 'Launcher must publish as one executable.' }
    $exePath = Join-Path $repoRoot 'dist\M365Collector-0.0.3-win-x64.exe'
    Copy-Item -LiteralPath (Join-Path $launcherOutput 'M365Collector.Launcher.exe') -Destination $exePath -Force
    $exeHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$exeHash  $([IO.Path]::GetFileName($exePath))" | Set-Content -LiteralPath ($exePath + '.sha256') -Encoding ascii
    $iconZip = Join-Path $repoRoot 'dist\M365Collector-0.0.3-icons.zip'
    if (Test-Path -LiteralPath $iconZip) { [IO.File]::Delete($iconZip) }
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $repoRoot 'assets\icons'),$iconZip)
    Write-Output "Package directory: $stagingPath"
    Get-Item -LiteralPath $zipPath,($zipPath + '.sha256'),$exePath,($exePath + '.sha256'),$iconZip | Select-Object Name,Length
} finally { Pop-Location }
