[CmdletBinding()]
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
$remote = & git remote get-url origin
if ($LASTEXITCODE -ne 0 -or $remote -notmatch 'github\.com[:/]jacksonsystems59/m365collector(?:\.git)?$') { throw 'Wrong repository remote. Build stopped.' }
$version = '0.1.0'
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + ($Arguments -join ' ')) }
}
Invoke-DotNet @('clean','M365Collector.sln','-c','Release','--nologo')
Invoke-DotNet @('restore','M365Collector.sln','--locked-mode')
Invoke-DotNet @('build','M365Collector.sln','-c','Release','--no-restore','--nologo')
if (-not $SkipTests) { Invoke-DotNet @('test','M365Collector.sln','-c','Release','--no-build','--logger','trx;LogFileName=release.trx') }
$distRoot = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
$packageRoot = Join-Path $distRoot "M365Collector-$version-win-x64"
if (Test-Path -LiteralPath $packageRoot) {
    $resolvedPackage = [IO.Path]::GetFullPath($packageRoot)
    $resolvedDist = [IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
    if (-not $resolvedPackage.StartsWith($resolvedDist,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package cleanup target.' }
    Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
}
Invoke-DotNet @('publish','src/M365Collector.GUI/M365Collector.GUI.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$packageRoot)
Invoke-DotNet @('publish','src/M365Collector.Service/M365Collector.Service.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:RestoreLockedMode=true','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-o',(Join-Path $packageRoot 'service'))
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'CHANGELOG.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $packageRoot -Recurse
$files = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($packageRoot,$_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ schemaVersion=1; product='M365Collector'; version=$version; runtimeIdentifier='win-x64'; guiExecutable='M365Collector.exe'; serviceExecutable='service/M365Collector.Service.exe'; configSchema=1; databaseSchema=1; files=$files }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'release.json') -Encoding utf8
& (Join-Path $packageRoot 'service/M365Collector.Service.exe') --verify-package $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Published package failed manifest verification.' }
$versionResult = & (Join-Path $packageRoot 'service/M365Collector.Service.exe') --version | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $versionResult.version -ne $version) { throw 'Published service version does not match the asset version.' }
$asset = Join-Path $distRoot "M365Collector-$version-win-x64.zip"
if (Test-Path -LiteralPath $asset) { Remove-Item -LiteralPath $asset -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageRoot,$asset,[IO.Compression.CompressionLevel]::Optimal,$false)
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($asset))" | Set-Content -LiteralPath ($asset + '.sha256') -Encoding ascii
Write-Output "Validation package: $asset"
Write-Output 'Publishing is separate. Complete Windows/Entra acceptance, commit/push matching source, and obtain confirmation before publishing.'
