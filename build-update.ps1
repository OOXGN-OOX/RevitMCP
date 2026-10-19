param([Parameter(Mandatory)][string]$BuildId, [long]$Sequence = 0, [ValidateSet('stable','beta')][string]$Channel = 'beta', [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
if ($BuildId -notmatch '^[a-f0-9]{40}$' -or $Sequence -lt 0) { throw 'Invalid build identity.' }
$stage = Join-Path $PSScriptRoot ('installer/obj/runtime-' + [guid]::NewGuid().ToString('N'))
node (Join-Path $PSScriptRoot 'installer/runtime.mjs') $PSScriptRoot $stage $BuildId $Sequence
if ($LASTEXITCODE -ne 0) { throw 'Runtime packaging failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $stage 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.channel -ne $Channel) { throw 'The requested update channel does not match the source branch.' }
$currentCommit = (git -C $PSScriptRoot rev-parse HEAD).Trim()
if ($BuildId -ne $currentCommit) { throw 'BuildId must match the source commit.' }
$site = if ($Channel -eq 'beta') { Join-Path $OutputDirectory 'site/updates/beta' } else { $OutputDirectory }
New-Item -ItemType Directory -Force -Path $site | Out-Null
$archive = Join-Path $site "RevitMCP-$BuildId-Update.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
$packageUrl = if ($Channel -eq 'beta') { "https://ooxgn-oox.github.io/RevitMCP/updates/beta/RevitMCP-$BuildId-Update.zip" } else { "https://github.com/OOXGN-OOX/RevitMCP/releases/download/v$($manifest.version)/RevitMCP-$BuildId-Update.zip" }
$feed = @{ manifest = $manifest; packageUrl = $packageUrl; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $archive).Length }
$feedName = if ($Channel -eq 'beta') { 'manifest.json' } else { 'update.json' }
$feedPath = Join-Path $site $feedName
$feed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $feedPath -Encoding utf8NoBOM
if ($Channel -eq 'stable') {
    $hashes = foreach ($file in $archive,$feedPath) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),(Split-Path $file -Leaf) }
    Add-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Value $hashes -Encoding utf8NoBOM
}
