[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'dist'))
$archiveName = 'LocalResourceLibrary-WinUI-trial-source'
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $distRoot ($archiveName + '.zip')))
$runSuffix = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$temporaryArchive = [System.IO.Path]::GetFullPath((Join-Path $distRoot ('.source-' + $runSuffix + '.zip')))
$excludedDirectories = @('bin', 'obj', '.git', '.vs', '.idea', 'dist', 'artifacts', 'TestResults', 'node_modules', 'data', 'userdata', 'user-data', 'logs', 'settings')

function Assert-DistPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $distRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Archive operation must stay inside the repository dist directory: $resolvedPath"
    }
}

function Get-SourceFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string[]]$Extensions
    )

    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        # Do not follow junctions or links into unrelated user data.
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin $excludedDirectories -and -not $entry.Name.StartsWith('.')) {
                Get-SourceFiles -Directory $entry.FullName -Extensions $Extensions
            }
            continue
        }
        if ($entry.Extension.ToLowerInvariant() -notin $Extensions) { continue }
        if ($entry.Name -match '(?i)(^|[._-])(settings|appsettings|credentials|secrets)([._-]|$)') { continue }
        if ($entry.Name -match '(?i)\.(user|suo|db|sqlite|sqlite3|log|dmp)(?:-(shm|wal|journal))?$') { continue }
        $entry.FullName
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'LocalResourceLibrary.WinUI.slnx') -PathType Leaf)) {
    throw 'Run this script from its original scripts directory inside the source repository.'
}

$null = New-Item -ItemType Directory -Path $distRoot -Force
if (((Get-Item -LiteralPath $distRoot).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The dist output directory must not be a junction or symbolic link.'
}
Assert-DistPath -Path $archivePath
Assert-DistPath -Path $temporaryArchive

$sourcePaths = [System.Collections.Generic.List[string]]::new()
$rootFiles = @(
    'README.md', 'README.en.md', 'THIRD-PARTY-NOTICES.md', 'LocalResourceLibrary.WinUI.slnx',
    '.gitignore', '.gitattributes', '.editorconfig', 'global.json', 'NuGet.config',
    'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
    'LICENSE', 'LICENSE.md', 'LICENSE.txt'
)
foreach ($relativePath in $rootFiles) {
    $fullPath = Join-Path $repositoryRoot $relativePath
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        if (((Get-Item -LiteralPath $fullPath).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $sourcePaths.Add($fullPath)
    }
}

$folderRules = @(
    @{ Name = 'src'; Extensions = @('.cs', '.csproj', '.xaml', '.resx', '.manifest', '.props', '.targets', '.json', '.md', '.txt', '.ico', '.png', '.svg') },
    @{ Name = 'tests'; Extensions = @('.cs', '.csproj', '.xaml', '.resx', '.manifest', '.props', '.targets', '.json', '.md', '.txt') },
    @{ Name = 'scripts'; Extensions = @('.ps1', '.psm1', '.psd1', '.py', '.cmd', '.bat', '.sh', '.md') },
    @{ Name = 'docs'; Extensions = @('.md', '.txt', '.png', '.svg') }
)
foreach ($rule in $folderRules) {
    $folder = Join-Path $repositoryRoot $rule.Name
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
    if (((Get-Item -LiteralPath $folder).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
    foreach ($fullPath in Get-SourceFiles -Directory $folder -Extensions $rule.Extensions) {
        $sourcePaths.Add($fullPath)
    }
}

[string[]]$relativePaths = @($sourcePaths | ForEach-Object {
    $fullPath = [System.IO.Path]::GetFullPath($_)
    if (-not $fullPath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "A source path escaped the repository: $fullPath"
    }
    $fullPath.Substring($repositoryPrefix.Length)
})
[Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)
foreach ($requiredPath in @('LocalResourceLibrary.WinUI.slnx', 'src\LocalResourceLibrary.WinUI\LocalResourceLibrary.WinUI.csproj', 'src\LocalResourceLibrary.WinUI\Localization\Localizer.cs', 'src\LocalResourceLibrary.WinUI\Services\SettingsStore.cs', 'src\LocalResourceLibrary.WinUI\Services\ErrorText.cs', 'src\LocalResourceLibrary.Core\LocalResourceLibrary.Core.csproj')) {
    if ($requiredPath -notin $relativePaths) { throw "Required source file is missing: $requiredPath" }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stableTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$archive = [System.IO.Compression.ZipFile]::Open($temporaryArchive, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relativePath in $relativePaths) {
        $entryName = $archiveName + '/' + $relativePath.Replace('\', '/')
        $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stableTimestamp
        $inputStream = [System.IO.File]::OpenRead((Join-Path $repositoryRoot $relativePath))
        try {
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose() }
        }
        finally { $inputStream.Dispose() }
    }
}
finally { $archive.Dispose() }

$readback = [System.IO.Compression.ZipFile]::OpenRead($temporaryArchive)
try {
    if ($readback.Entries.Count -ne $relativePaths.Count) { throw 'Source archive entry count did not match the selected files.' }
    foreach ($entry in $readback.Entries) {
        if (-not $entry.FullName.StartsWith($archiveName + '/', [System.StringComparison]::Ordinal) -or $entry.FullName.Contains('../')) {
            throw "Unexpected archive entry: $($entry.FullName)"
        }
    }
}
finally { $readback.Dispose() }

if (Test-Path -LiteralPath $archivePath) {
    $previousArchive = [System.IO.Path]::GetFullPath((Join-Path $distRoot ('.previous-winui-source-' + $runSuffix + '.zip')))
    Assert-DistPath -Path $previousArchive
    Move-Item -LiteralPath $archivePath -Destination $previousArchive
    Write-Host "Previous source archive retained at $previousArchive"
}
Move-Item -LiteralPath $temporaryArchive -Destination $archivePath
Write-Host "Source archive: $archivePath"
Write-Host "Included $($relativePaths.Count) files with a stable order and entry timestamps."
