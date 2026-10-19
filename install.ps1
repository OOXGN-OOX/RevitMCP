param([switch]$EnableAutoConfirm)
$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot 'plugin\bin\Release\net8.0-windows\RevitMCP.dll'
$projectFile = Join-Path $PSScriptRoot 'plugin\RevitMCP.csproj'
$manifestName = 'RevitMCP.addin'
$addinsDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$pluginDirectory = Join-Path $addinsDirectory 'RevitMCP'
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = $project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Некорректная версия надстройки в $($projectFile): '$version'." }
$versionDirectory = Join-Path $pluginDirectory "versions\$version"
$installedAssembly = Join-Path $versionDirectory 'RevitMCP.dll'
$manifestPath = Join-Path $addinsDirectory $manifestName

if (-not (Test-Path $assembly)) {
    throw "Не найден $assembly. Сначала выполните .\build.ps1."
}

$revitRunning = [bool](Get-Process -Name Revit -ErrorAction SilentlyContinue)
if ($revitRunning -and (Test-Path -LiteralPath $installedAssembly)) {
    throw "Revit запущен, а сборка версии $version уже установлена. Закройте Revit перед повторной установкой этой версии."
}

New-Item -ItemType Directory -Force -Path $versionDirectory | Out-Null
Copy-Item -LiteralPath $assembly -Destination $installedAssembly -Force

$assemblyPath = [System.Security.SecurityElement]::Escape($installedAssembly)
$manifest = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit MCP</Name>
    <Assembly>$assemblyPath</Assembly>
    <AddInId>4D213781-9614-4BB6-9B2C-1F102A68B174</AddInId>
    <FullClassName>RevitMCP.PluginApplication</FullClassName>
    <VendorId>OOXG</VendorId>
    <VendorDescription>OOXGN — Revit MCP local bridge</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

New-Item -ItemType Directory -Force -Path $addinsDirectory | Out-Null
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8
if ($EnableAutoConfirm) {
    $settingsDirectory = Join-Path $env:LOCALAPPDATA 'RevitMCP'
    $settingsPath = Join-Path $settingsDirectory 'settings.json'
    New-Item -ItemType Directory -Force -Path $settingsDirectory | Out-Null
    $settings = if (Test-Path -LiteralPath $settingsPath) {
        Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    $settings | Add-Member -NotePropertyName autoConfirm -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 20))
    Write-Host 'Автоподтверждение команд apply включено в локальных настройках.'
}
if ($revitRunning) {
    Write-Warning "Версия $version подготовлена для следующего запуска; уже открытый Revit продолжит работу с загруженным состоянием."
} else {
    Write-Host "Версия $version установлена. Запустите Revit 2026 и разрешите загрузку надстройки, если появится запрос."
}
