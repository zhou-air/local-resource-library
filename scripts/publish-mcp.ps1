[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts/LocalResourceLibrary-Mcp-win-x64'
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    if ((Get-Item -LiteralPath $output).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw 'MCP output must not be a directory link.'
    }
}
& dotnet publish (Join-Path $projectRoot 'src/LocalResourceLibrary.Mcp/LocalResourceLibrary.Mcp.csproj') -c Release -r win-x64 --self-contained true '-p:DebugType=None' '-p:DebugSymbols=false' -o $output
if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/mcp.md') -Destination (Join-Path $output 'README-MCP.md') -Force
New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/mcp-verification.md') -Destination (Join-Path $output 'docs/mcp-verification.md') -Force
New-Item -ItemType Directory -Path (Join-Path $output 'docs/licenses') -Force | Out-Null
foreach ($license in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs/licenses') -File) {
    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $output 'docs/licenses') -Force
}

# Include licenses from the exact packages and runtime used in this standalone distribution.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/LocalResourceLibrary.Mcp/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    foreach ($packageRoot in $packageRoots) {
        $packageDirectory = Join-Path $packageRoot $library.Value.path
        if (-not (Test-Path -LiteralPath $packageDirectory)) { continue }
        $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|SDK_LICENSE|NOTICE|THIRD.PARTY.NOTICES)(\.|$)' })
        if ($notices.Count -gt 0) {
            $noticeDirectory = Join-Path $output ('docs/licenses/packages/' + $library.Name)
            New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
            foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $noticeDirectory -Force }
        }
        break
    }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $output 'LocalResourceLibrary.Mcp.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $runtimePack = @($packageRoots | ForEach-Object { Join-Path $_ ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version) }) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $runtimePack) { throw "Runtime notices not found for $($framework.name) $($framework.version)." }
    $noticeDirectory = Join-Path $output ('docs/licenses/runtime/' + $framework.name)
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
    foreach ($notice in Get-ChildItem -LiteralPath $runtimePack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination $noticeDirectory -Force
    }
}

$executable = (Join-Path $output 'LocalResourceLibrary.Mcp.exe').Replace('\', '/')
if ($executable.Contains('"') -or $executable.Contains("`n") -or $executable.Contains("`r")) { throw 'Output path cannot be represented in the generated configuration.' }
$configuration = @"
# Append to your Codex config.toml; this starts the server only when the client needs it.
[mcp_servers.local_resource_library]
command = "$executable"
args = ["--allow-batch-commit"]
startup_timeout_sec = 30
tool_timeout_sec = 60

# Require explicit client approval for each batch. The token alone is not human approval.
[mcp_servers.local_resource_library.tools.commit_resource_updates]
approval_mode = "prompt"
"@
[System.IO.File]::WriteAllText((Join-Path $output 'codex-mcp.toml'), $configuration + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "MCP folder: $output"
Write-Host "Codex configuration: $(Join-Path $output 'codex-mcp.toml')"
