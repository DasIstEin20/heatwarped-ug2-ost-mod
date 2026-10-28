param([string]$Compiler = '')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $Compiler) {
    $found = Get-Command gcc.exe -ErrorAction SilentlyContinue
    if ($found) { $Compiler = $found.Source }
    elseif (Test-Path -LiteralPath 'C:\msys64\mingw64\bin\gcc.exe') { $Compiler = 'C:\msys64\mingw64\bin\gcc.exe' }
    else { throw 'Install a MinGW-w64 x64 GCC compiler or pass -Compiler with its full path.' }
}
$env:PATH = (Split-Path -Path $Compiler -Parent) + ';' + $env:PATH
New-Item -ItemType Directory -Path 'release-assets','dist\1.2.0' -Force | Out-Null
& $Compiler -shared -O2 -s -Wall -Wextra -static-libgcc -o release-assets\version.dll native\music_proxy.c native\version.def vendor\minhook\src\buffer.c vendor\minhook\src\hook.c vendor\minhook\src\trampoline.c vendor\minhook\src\hde\hde64.c -lshell32 -luser32
if ($LASTEXITCODE -ne 0) { throw 'Native loader compilation failed.' }
dotnet publish installer\HeatwarpedUG2Mod.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist\1.2.0\publish
if ($LASTEXITCODE -ne 0) { throw 'Installer publication failed.' }
Copy-Item -LiteralPath 'dist\1.2.0\publish\HeatwarpedUG2Mod.exe' -Destination 'dist\1.2.0\HeatwarpedUG2Mod.exe' -Force
Copy-Item -LiteralPath 'release-assets\README.txt' -Destination 'dist\1.2.0\README.txt' -Force
Copy-Item -LiteralPath 'LICENSE' -Destination 'dist\1.2.0\LICENSE' -Force
Copy-Item -LiteralPath 'release-assets\THIRD-PARTY-NOTICES.txt' -Destination 'dist\1.2.0\THIRD-PARTY-NOTICES.txt' -Force

# Explicit allowlists keep locally extracted audio, game files, caches and
# machine-specific development helpers out of both distributable archives.
Add-Type -AssemblyName System.IO.Compression
function Write-ReleaseArchive([string]$Destination, [string[]]$Files, [string]$Prefix = '') {
    $archivePath = [IO.Path]::GetFullPath($Destination, $PWD.Path)
    $expectedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist\1.2.0')) + [IO.Path]::DirectorySeparatorChar
    if (-not $archivePath.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive destination escapes the release directory.' }
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $Files) {
            $entryName = ($Prefix + $file).Replace('\','/')
            $sourcePath = (Resolve-Path -LiteralPath $file).ProviderPath
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $sourcePath, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
$releaseFiles = @('HeatwarpedUG2Mod.exe','README.txt','LICENSE','THIRD-PARTY-NOTICES.txt')
Push-Location -LiteralPath 'dist\1.2.0'
try { Write-ReleaseArchive (Join-Path $PWD 'Heatwarped-UG2-OST-Mod-1.2.0-win-x64.zip') $releaseFiles }
finally { Pop-Location }
$sourceFiles = @('build-release.ps1','test-release.ps1','test-player.ps1','BUILDING.txt','LICENSE','.gitignore','release-assets\README.txt','release-assets\THIRD-PARTY-NOTICES.txt','tests\fmod_integration.c','tests\player_state.c','tests\proxy_smoke.c')
foreach ($folder in @('core','installer','settings','native','vendor\minhook','tests\CoreTests')) {
    $sourceFiles += Get-ChildItem -LiteralPath $folder -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object { [IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName) }
}
Write-ReleaseArchive 'dist\1.2.0\Heatwarped-UG2-OST-Mod-1.2.0-source.zip' ($sourceFiles | Sort-Object -Unique) 'HeatwarpedUG2Mod/'
$hashFiles = @('HeatwarpedUG2Mod.exe','Heatwarped-UG2-OST-Mod-1.2.0-win-x64.zip','Heatwarped-UG2-OST-Mod-1.2.0-source.zip')
$hashLines = foreach ($file in $hashFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path 'dist\1.2.0' $file) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $file"
}
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'dist\1.2.0\SHA256SUMS.txt'), $hashLines, [Text.UTF8Encoding]::new($false))
Write-Output 'Built installer, Windows x64 ZIP, source ZIP and SHA256SUMS.txt in dist\1.2.0.'
