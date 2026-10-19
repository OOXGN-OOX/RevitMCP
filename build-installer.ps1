param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'), [switch]$UseRevitNuGet, [switch]$SkipPluginBuild)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js LTS is required on the build computer.' }
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin\RevitMCP.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
node (Join-Path $PSScriptRoot 'installer\check-release.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Release identity or version mismatch.' }
$sourceArchive = Join-Path $OutputDirectory "RevitMCP-$version-source.zip"
$commit = node (Join-Path $PSScriptRoot 'installer/source.mjs') $PSScriptRoot $sourceArchive
if ($LASTEXITCODE -ne 0) { throw 'Source archive creation failed.' }
if (!$SkipPluginBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -UseRevitNuGet:$UseRevitNuGet }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'server\node_modules\zod'))) {
    Push-Location (Join-Path $PSScriptRoot 'server')
    try { pnpm install --frozen-lockfile; if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' } }
    finally { Pop-Location }
}
$stageRoot = Join-Path $PSScriptRoot ('installer\obj\package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stageRoot 'payload'
New-Item -ItemType Directory -Force -Path $payload,$OutputDirectory | Out-Null
node (Join-Path $PSScriptRoot 'installer\package.mjs') $PSScriptRoot $payload
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
$sequence = if ($env:GITHUB_RUN_NUMBER) { $env:GITHUB_RUN_NUMBER } else { '0' }
node (Join-Path $PSScriptRoot 'installer/runtime.mjs') $PSScriptRoot (Join-Path $payload 'runtime') $commit $sequence
if ($LASTEXITCODE -ne 0) { throw 'Runtime packaging failed.' }
node (Join-Path $PSScriptRoot 'installer\smoke.mjs') (Join-Path $payload 'node\node.exe') (Join-Path $payload 'server\src\index.js')
if ($LASTEXITCODE -ne 0) { throw 'Packaged MCP smoke check failed.' }
New-Item -ItemType Directory -Path (Join-Path $payload 'source') -Force | Out-Null
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $payload 'source')
$payloadArchive = Join-Path $stageRoot 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadArchive -CompressionLevel Optimal
dotnet publish (Join-Path $PSScriptRoot 'installer\RevitMCP.Setup.csproj') -c Release -o (Join-Path $stageRoot 'publish') "-p:PayloadPath=$payloadArchive" --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$setup = Join-Path $OutputDirectory "RevitMCP-$version-Setup.exe"
Copy-Item -LiteralPath (Join-Path $stageRoot 'publish\RevitMCP.Setup.exe') -Destination $setup -Force
$hashLines = foreach ($file in $setup,$sourceArchive) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $hashLines)
Write-Host "Installer: $setup"
Write-Host "Sources:   $sourceArchive"
