param([Parameter(Mandatory)][string]$BuildId, [long]$Sequence = 0, [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
if ($BuildId -notmatch '^[a-f0-9]{40}$' -or $Sequence -lt 0) { throw 'Invalid build identity.' }
$stage = Join-Path $PSScriptRoot ('installer/obj/runtime-' + [guid]::NewGuid().ToString('N'))
node (Join-Path $PSScriptRoot 'installer/runtime.mjs') $PSScriptRoot $stage $BuildId $Sequence
if ($LASTEXITCODE -ne 0) { throw 'Runtime packaging failed.' }
$site = Join-Path $OutputDirectory 'site/updates/0.0.2'
New-Item -ItemType Directory -Force -Path $site | Out-Null
$archive = Join-Path $site "RevitMCP-$BuildId-Update.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
$manifest = Get-Content -LiteralPath (Join-Path $stage 'manifest.json') -Raw | ConvertFrom-Json
$feed = @{ manifest = $manifest; packageUrl = "https://ooxgn-oox.github.io/RevitMCP/updates/0.0.2/RevitMCP-$BuildId-Update.zip"; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $archive).Length }
$feed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $site 'manifest.json') -Encoding utf8NoBOM
