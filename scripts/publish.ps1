[CmdletBinding()]
param(
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'dist'))
$appProject = Join-Path $repositoryRoot 'src\LocalResourceLibrary.App\LocalResourceLibrary.App.csproj'
$releaseName = 'LocalResourceLibrary-win-x64'
$releaseDirectory = [System.IO.Path]::GetFullPath((Join-Path $distRoot $releaseName))
$runSuffix = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$stagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $distRoot ('.staging-' + $runSuffix)))

function Assert-ReleasePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $distRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release operation must stay inside the repository dist directory: $resolvedPath"
    }
}

if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    throw "Application project was not found: $appProject"
}

$null = Get-Command dotnet -ErrorAction Stop
$null = New-Item -ItemType Directory -Path $distRoot -Force
if (((Get-Item -LiteralPath $distRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The dist output directory must not be a junction or symbolic link.'
}
Assert-ReleasePath -Path $stagingDirectory
Assert-ReleasePath -Path $releaseDirectory

Write-Host 'Publishing a self-contained Windows x64 application...'
& dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained true --output $stagingDirectory '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishSingleFile=false'
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE). Any partial output remains in $stagingDirectory"
}

if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory 'LocalResourceLibrary.exe') -PathType Leaf)) {
    throw 'Publishing completed without the expected LocalResourceLibrary.exe.'
}

foreach ($document in @('README.md', 'README.en.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
    $sourcePath = Join-Path $repositoryRoot $document
    if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $stagingDirectory $document)
    }
}

$documentationDirectory = Join-Path $repositoryRoot 'docs'
if (Test-Path -LiteralPath $documentationDirectory -PathType Container) {
    Copy-Item -LiteralPath $documentationDirectory -Destination (Join-Path $stagingDirectory 'docs') -Recurse
}

# Self-contained runtime packs keep redistribution notices at their package root.
# Copy from the exact versions in the resulting runtime configuration.
$runtimeConfigurationPath = Join-Path $stagingDirectory 'LocalResourceLibrary.runtimeconfig.json'
$runtimeConfiguration = Get-Content -LiteralPath $runtimeConfigurationPath -Raw | ConvertFrom-Json
$restoreAssets = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\LocalResourceLibrary.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($restoreAssets.packageFolders.PSObject.Properties.Name)
$dotnetDirectory = Split-Path (Get-Command dotnet).Source -Parent
foreach ($framework in $runtimeConfiguration.runtimeOptions.includedFrameworks) {
    $packName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $packCandidates = @($packageRoots | ForEach-Object { Join-Path $_ ($packName + '\' + $framework.version) })
    $packCandidates += Join-Path $dotnetDirectory ('packs\' + $framework.name + '.Runtime.win-x64\' + $framework.version)
    $runtimePack = $packCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $runtimePack) {
        throw "Cannot locate redistribution notices for $($framework.name) $($framework.version). Staging output remains at $stagingDirectory"
    }
    $notices = @(Get-ChildItem -LiteralPath $runtimePack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)' })
    if ($notices.Count -eq 0) {
        throw "No runtime redistribution notices found in $runtimePack"
    }
    $noticeDestination = Join-Path $stagingDirectory ('docs\licenses\runtime\' + $framework.name)
    $null = New-Item -ItemType Directory -Path $noticeDestination -Force
    foreach ($notice in $notices) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $noticeDestination $notice.Name)
    }
}

if (Test-Path -LiteralPath $releaseDirectory) {
    $previousDirectory = [System.IO.Path]::GetFullPath((Join-Path $distRoot ('.previous-' + $runSuffix)))
    Assert-ReleasePath -Path $previousDirectory
    Move-Item -LiteralPath $releaseDirectory -Destination $previousDirectory
    Write-Host "Previous release retained at $previousDirectory"
}

Move-Item -LiteralPath $stagingDirectory -Destination $releaseDirectory
Write-Host "Published folder: $releaseDirectory"

if ($Zip) {
    $archivePath = [System.IO.Path]::GetFullPath((Join-Path $distRoot ($releaseName + '.zip')))
    Assert-ReleasePath -Path $archivePath
    $stagingArchive = [System.IO.Path]::GetFullPath((Join-Path $distRoot ('.archive-' + $runSuffix + '.zip')))
    Assert-ReleasePath -Path $stagingArchive
    Compress-Archive -LiteralPath $releaseDirectory -DestinationPath $stagingArchive -CompressionLevel Optimal
    if (Test-Path -LiteralPath $archivePath) {
        $previousArchive = [System.IO.Path]::GetFullPath((Join-Path $distRoot ('.previous-' + $runSuffix + '.zip')))
        Assert-ReleasePath -Path $previousArchive
        Move-Item -LiteralPath $archivePath -Destination $previousArchive
    }
    Move-Item -LiteralPath $stagingArchive -Destination $archivePath
    Write-Host "Published archive: $archivePath"
}
