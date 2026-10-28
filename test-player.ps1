param([string]$Compiler = '', [string]$LoaderPath = '')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (-not $Compiler) {
    $found = Get-Command gcc.exe -ErrorAction SilentlyContinue
    if ($found) { $Compiler = $found.Source }
    elseif (Test-Path -LiteralPath 'C:\msys64\mingw64\bin\gcc.exe') { $Compiler = 'C:\msys64\mingw64\bin\gcc.exe' }
    else { throw 'Install MinGW-w64 x64 GCC or pass -Compiler.' }
}
$env:PATH = (Split-Path -Path $Compiler -Parent) + ';' + $env:PATH
New-Item -ItemType Directory -Path analysis -Force | Out-Null
& $Compiler -O2 -Wall -Wextra -Werror -static-libgcc -o analysis\player-state.exe tests\player_state.c vendor\minhook\src\buffer.c vendor\minhook\src\hook.c vendor\minhook\src\trampoline.c vendor\minhook\src\hde\hde64.c -lshell32 -luser32 -lm
if ($LASTEXITCODE -ne 0) { throw 'Player tests did not compile.' }
& .\analysis\player-state.exe
if ($LASTEXITCODE -ne 0) { throw 'Player state tests failed.' }
if ($LoaderPath) {
    $loader = (Resolve-Path -LiteralPath $LoaderPath).ProviderPath
    & $Compiler -O2 -Wall -Wextra -Werror -o analysis\proxy-smoke.exe tests\proxy_smoke.c
    if ($LASTEXITCODE -ne 0) { throw 'Proxy smoke test did not compile.' }
    & .\analysis\proxy-smoke.exe $loader
    if ($LASTEXITCODE -ne 0) { throw 'Windows version API forwarding failed.' }
}
