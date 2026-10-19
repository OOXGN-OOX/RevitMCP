$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'FixtureCore/FixtureCore.csproj'
$output = Join-Path $PSScriptRoot 'Updates.Tests/obj/fixtures'
foreach ($variant in 'A','B') {
    dotnet build $fixture -c Release "-p:Variant=$variant" -o (Join-Path $output $variant) --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
}
dotnet run --project (Join-Path $PSScriptRoot 'Updates.Tests/Updates.Tests.csproj') -c Release -- (Join-Path $output 'A/RevitMCP.Core.dll') (Join-Path $output 'B/RevitMCP.Core.dll')
if ($LASTEXITCODE -ne 0) { throw 'Update tests failed.' }
