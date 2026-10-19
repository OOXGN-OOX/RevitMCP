param([string]$Revit2026Dir = 'C:\Program Files\Autodesk\Revit 2026', [switch]$UseRevitNuGet)
$ErrorActionPreference = 'Stop'
foreach ($project in 'plugin/RevitMCP.csproj','loader/RevitMCP.Loader.csproj') {
    $arguments = @('build', (Join-Path $PSScriptRoot $project), '-c', 'Release', '--verbosity', 'minimal')
    if ($UseRevitNuGet) { $arguments += '-p:UseRevitNuGet=true' }
    else { $arguments += "-p:Revit2026Dir=$Revit2026Dir" }
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
