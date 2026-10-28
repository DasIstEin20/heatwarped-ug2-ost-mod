param(
    [Parameter(Mandatory=$true)][string]$NfsFolder,
    [Parameter(Mandatory=$true)][string]$GameFolder,
    [Parameter(Mandatory=$true)][string]$DecoderZip,
    [string]$Compiler = '',
    [switch]$NativeOnly
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $NativeOnly) {
    dotnet build tests\CoreTests\CoreTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    dotnet tests\CoreTests\bin\Release\net8.0-windows\CoreTests.dll $NfsFolder $GameFolder $DecoderZip (Join-Path $PSScriptRoot 'dist\1.2.0\HeatwarpedUG2Mod.exe') (Join-Path $PSScriptRoot 'analysis\installer-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Installer integration tests failed.' }
}
$report = Get-Content -LiteralPath 'analysis\installer-tests\test-results.json' -Raw | ConvertFrom-Json
if (-not $report.passed) { throw 'A passing integration fixture is required.' }
$fixture = [IO.Path]::GetFullPath($report.testRoot)
$ownedParent = Split-Path -Path $fixture -Parent
$expectedParent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'analysis\installer-tests'))
if ((Split-Path -Path $ownedParent -Parent) -ne $expectedParent -or
    (Split-Path -Path $ownedParent -Leaf) -notmatch '^work-[0-9a-f]{32}$' -or
    (Get-Content -LiteralPath (Join-Path $ownedParent '.heatwarped-ug2-owned') -Raw) -ne 'Heatwarped.UG2.OST') {
    throw 'The native test must run in an owned isolated test fixture.'
}
$appInfo = Get-Content -LiteralPath (Join-Path $fixture 'Heatwarped_Data\app.info')
if ($appInfo[0] -ne 'HeatwarpedModTests') { throw 'Refusing to modify a real game installation.' }
if (-not (Test-Path -LiteralPath (Join-Path $fixture 'CustomMusic'))) {
    $disabled = @(Get-ChildItem -LiteralPath $fixture -Directory -Filter 'CustomMusic.disabled-*')
    if ($disabled.Count -ne 1) { throw 'Expected exactly one disabled test mod.' }
    Move-Item -LiteralPath $disabled[0].FullName -Destination (Join-Path $fixture 'CustomMusic')
    Move-Item -LiteralPath (Join-Path $fixture 'CustomMusic\version.disabled.dll') -Destination (Join-Path $fixture 'version.dll')
}
$changedBank = Join-Path $fixture 'Heatwarped_Data\StreamingAssets\Master.bank.updated'
if (Test-Path -LiteralPath $changedBank) {
    Move-Item -LiteralPath $changedBank -Destination (Join-Path $fixture 'Heatwarped_Data\StreamingAssets\Master.bank')
}
if (-not $Compiler) {
    $found = Get-Command gcc.exe -ErrorAction SilentlyContinue
    if ($found) { $Compiler = $found.Source }
    elseif (Test-Path -LiteralPath 'C:\msys64\mingw64\bin\gcc.exe') { $Compiler = 'C:\msys64\mingw64\bin\gcc.exe' }
    else { throw 'Install MinGW-w64 x64 GCC or pass -Compiler.' }
}
$env:PATH = (Split-Path -Path $Compiler -Parent) + ';' + $env:PATH
$testExe = Join-Path $fixture 'fmod-integration.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release-assets\version.dll') -Destination (Join-Path $fixture 'version.dll') -Force
# Some GCC driver builds cannot pass Unicode output paths to their linker.
$buildExe = Join-Path $PSScriptRoot 'analysis\fmod-integration.exe'
& $Compiler -O2 -Wall -Wextra -o $buildExe tests\fmod_integration.c -lm
if ($LASTEXITCODE -ne 0) { throw 'Native integration test build failed.' }
Copy-Item -LiteralPath $buildExe -Destination $testExe -Force
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Native audio integration test failed.' }
$report | Add-Member -NotePropertyName nativeAudio -NotePropertyValue $true -Force
$report | ConvertTo-Json | Set-Content -LiteralPath 'analysis\installer-tests\test-results.json' -Encoding utf8
Write-Output 'PASS: release loader in isolated fixture (FMOD no-sound output; no game launched).'
