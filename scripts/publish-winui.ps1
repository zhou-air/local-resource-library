[CmdletBinding()]
param([switch]$Zip, [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
$releaseName = 'LocalResourceLibrary-WinUI-win-x64'
$releaseDirectory = Join-Path $distRoot $releaseName
$suffix = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
function Assert-DistPath([string]$Path) {
    $absolute = [System.IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($distRoot.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected release path: $absolute" }
}
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if ((Get-Item -LiteralPath $distRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'dist must not be a directory link.' }
Assert-DistPath $releaseDirectory
if ($SkipPublish) {
    $output = $releaseDirectory
} else {
    $output = Join-Path $distRoot ('.winui-staging-' + $suffix)
    Assert-DistPath $output
    & dotnet publish (Join-Path $projectRoot 'src/LocalResourceLibrary.WinUI/LocalResourceLibrary.WinUI.csproj') -c Release -r win-x64 --self-contained true '-p:Platform=x64' '-p:WindowsAppSDKSelfContained=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o $output
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed; staging directory retained.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $output 'LocalResourceLibrary.WinUI.exe'))) { throw 'WinUI executable is missing.' }
if ((Get-Item -LiteralPath $output).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Release output must not be a directory link.' }

$docsDestination = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docsDestination -Force | Out-Null
foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs')) { Copy-Item -LiteralPath $entry.FullName -Destination $docsDestination -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/winui-trial.md') -Destination (Join-Path $output 'README-WINUI.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
foreach ($licenseFile in @('LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
    $licensePath = Join-Path $projectRoot $licenseFile
    if (Test-Path -LiteralPath $licensePath -PathType Leaf) { Copy-Item -LiteralPath $licensePath -Destination $output -Force }
}

# Keep licenses/notices from the exact restored packages with this new variant.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/LocalResourceLibrary.WinUI/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    foreach ($packageRoot in $packageRoots) {
        $packageDirectory = Join-Path $packageRoot $library.Value.path
        if (-not (Test-Path -LiteralPath $packageDirectory)) { continue }
        $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|SDK_LICENSE|NOTICE|THIRD.PARTY.NOTICES)(\.|$)' })
        if ($notices.Count -gt 0) {
            $noticeDirectory = Join-Path $docsDestination ('licenses/packages/' + $library.Name)
            New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
            foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $noticeDirectory -Force }
        }
        break
    }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $output 'LocalResourceLibrary.WinUI.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $runtimePack = @($packageRoots | ForEach-Object { Join-Path $_ ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version) }) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $runtimePack) { throw "Runtime notices pack not found: $($framework.name) $($framework.version)" }
    $noticeDirectory = Join-Path $docsDestination ('licenses/runtime/' + $framework.name)
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    foreach ($notice in Get-ChildItem -LiteralPath $runtimePack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)' }) { Copy-Item -LiteralPath $notice.FullName -Destination $noticeDirectory -Force }
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $releaseDirectory) {
        $previous = Join-Path $distRoot ('.previous-winui-' + $suffix)
        Assert-DistPath $previous
        Move-Item -LiteralPath $releaseDirectory -Destination $previous
    }
    Move-Item -LiteralPath $output -Destination $releaseDirectory
}
if ($Zip) {
    $archive = Join-Path $distRoot ($releaseName + '.zip')
    $stagingArchive = Join-Path $distRoot ('.winui-archive-' + $suffix + '.zip')
    Assert-DistPath $archive
    Assert-DistPath $stagingArchive
    Compress-Archive -LiteralPath $releaseDirectory -DestinationPath $stagingArchive -CompressionLevel Optimal
    if (Test-Path -LiteralPath $archive) {
        $previousArchive = Join-Path $distRoot ('.previous-winui-' + $suffix + '.zip')
        Assert-DistPath $previousArchive
        Move-Item -LiteralPath $archive -Destination $previousArchive
    }
    Move-Item -LiteralPath $stagingArchive -Destination $archive
    Write-Host "WinUI archive: $archive"
}
Write-Host "WinUI folder: $releaseDirectory"
