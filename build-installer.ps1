param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'), [switch]$UseRevitNuGet)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js LTS is required on the build computer.' }
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin\RevitMCP.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
node (Join-Path $PSScriptRoot 'installer\check-release.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Release identity or version mismatch.' }
$pluginAssembly = Join-Path $PSScriptRoot 'plugin\bin\Release\net8.0-windows\RevitMCP.dll'
$pluginInputs = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'plugin') -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
$pluginInputs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'build-fallback') -File
$pluginInputs += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'build.ps1')
$canReusePlugin = (Test-Path -LiteralPath $pluginAssembly) -and
    @($pluginInputs | Where-Object { $_.LastWriteTimeUtc -gt (Get-Item -LiteralPath $pluginAssembly).LastWriteTimeUtc }).Count -eq 0
if ($canReusePlugin) {
    $canReusePlugin = [Reflection.AssemblyName]::GetAssemblyName($pluginAssembly).Version.ToString() -eq "$version.0"
}
if ($canReusePlugin) { Write-Host "Using current plugin build: $pluginAssembly" }
else { & (Join-Path $PSScriptRoot 'build.ps1') -UseRevitNuGet:$UseRevitNuGet }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'server\node_modules\zod'))) {
    Push-Location (Join-Path $PSScriptRoot 'server')
    try { pnpm install --frozen-lockfile; if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' } }
    finally { Pop-Location }
}
$stageRoot = Join-Path $PSScriptRoot ('installer\obj\package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stageRoot 'payload'
$source = Join-Path $stageRoot 'source'
New-Item -ItemType Directory -Force -Path $payload,$source,$OutputDirectory | Out-Null
node (Join-Path $PSScriptRoot 'installer\package.mjs') $PSScriptRoot $payload
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
node (Join-Path $PSScriptRoot 'installer\smoke.mjs') (Join-Path $payload 'node\node.exe') (Join-Path $payload 'server\src\index.js')
if ($LASTEXITCODE -ne 0) { throw 'Packaged MCP smoke check failed.' }
# Use Git's tracked + nonignored files; never include runtime caches or private models.
Push-Location $PSScriptRoot
try { $sourceFiles = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard) }
finally { Pop-Location }
foreach ($relative in $sourceFiles) {
    if ($relative -match '(^|[\\/])(bin|obj|node_modules|dist|artifacts|work|\.agents|\.git)([\\/]|$)' -or $relative -match '\.(rvt|rfa|rte|rft|token|zip|exe|pdb)$') { continue }
    $inputFile = Join-Path $PSScriptRoot $relative
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { continue }
    $destination = Join-Path $source $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $inputFile -Destination $destination
}
$sourceArchive = Join-Path $OutputDirectory "RevitMCP-$version-source.zip"
Compress-Archive -Path (Join-Path $source '*') -DestinationPath $sourceArchive -Force
New-Item -ItemType Directory -Path (Join-Path $payload 'source') -Force | Out-Null
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $payload 'source')
$payloadArchive = Join-Path $stageRoot 'payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $payloadArchive -CompressionLevel Optimal
dotnet publish (Join-Path $PSScriptRoot 'installer\RevitMCP.Setup.csproj') -c Release -o (Join-Path $stageRoot 'publish') "-p:PayloadPath=$payloadArchive" --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$setup = Join-Path $OutputDirectory "RevitMCP-$version-Setup.exe"
Copy-Item -LiteralPath (Join-Path $stageRoot 'publish\RevitMCP.Setup.exe') -Destination $setup -Force
$prompts = Join-Path $OutputDirectory "RevitMCP-$version-asset-prompts.en.md"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\ASSET-PROMPTS.en.md') -Destination $prompts -Force
$graphicsRoot = Join-Path $stageRoot 'graphics-brief'
New-Item -ItemType Directory -Path (Join-Path $graphicsRoot 'docs\assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\ASSET-PROMPTS.en.md') -Destination (Join-Path $graphicsRoot 'docs')
Copy-Item -Path (Join-Path $PSScriptRoot 'docs\assets\*.svg') -Destination (Join-Path $graphicsRoot 'docs\assets')
$graphics = Join-Path $OutputDirectory "RevitMCP-$version-graphics-brief.zip"
Compress-Archive -Path (Join-Path $graphicsRoot 'docs') -DestinationPath $graphics -Force
$hashLines = foreach ($file in $setup,$sourceArchive,$prompts,$graphics) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $hashLines)
Write-Host "Installer: $setup"
Write-Host "Sources:   $sourceArchive"
