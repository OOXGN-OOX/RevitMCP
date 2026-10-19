param([string]$PackageDirectory = $PSScriptRoot, [switch]$Codex, [switch]$Claude)
$ErrorActionPreference = 'Stop'
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Для первой установки загрузчика закройте Revit. Последующие обновления доступны внутри плагина.' }
$version = (Get-Content -LiteralPath (Join-Path $PackageDirectory 'release.json') -Raw | ConvertFrom-Json).version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
$app = Join-Path $env:LOCALAPPDATA 'RevitMCP/Application'
$release = Join-Path $app "versions/$version"
$addin = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2026/RevitMCP.addin'
New-Item -ItemType Directory -Force -Path $release,(Split-Path $addin) | Out-Null
Get-ChildItem -LiteralPath $PackageDirectory | Where-Object Name -ne 'install.ps1' | Copy-Item -Destination $release -Recurse -Force
$manifest = Get-Content -LiteralPath (Join-Path $release 'runtime/manifest.json') -Raw | ConvertFrom-Json
@{relativePath="versions/$version/runtime";manifest=$manifest} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $app 'active.json') -Encoding utf8NoBOM
$assembly = [Security.SecurityElement]::Escape((Join-Path $release 'loader/RevitMCP.Loader.dll'))
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><RevitAddIns><AddIn Type=`"Application`"><Name>Revit MCP</Name><Assembly>$assembly</Assembly><AddInId>4D213781-9614-4BB6-9B2C-1F102A68B174</AddInId><FullClassName>RevitMCP.Loader.PluginApplication</FullClassName><VendorId>OOXG</VendorId><VendorDescription>OOXGN</VendorDescription></AddIn></RevitAddIns>"
if (Test-Path -LiteralPath $addin) { Copy-Item -LiteralPath $addin -Destination ($addin + '.' + [guid]::NewGuid().ToString('N') + '.bak') }
[IO.File]::WriteAllText($addin, $xml, [Text.UTF8Encoding]::new($false))
$clients = @('--examples')
if ($Codex) { $clients += '--codex' }; if ($Claude) { $clients += '--claude' }
& (Join-Path $release 'node/node.exe') (Join-Path $release 'configure-mcp.mjs') @clients
if ($LASTEXITCODE -ne 0) { throw 'MCP configuration failed.' }
Write-Host 'Revit MCP установлен. Запустите Revit 2026 и перезапустите MCP-клиент.'
