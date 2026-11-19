# Build the DSH Screen Ask native helpers with the .NET Framework 4 compiler.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/build-helper.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'helper'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc (install .NET Framework 4.x)"
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host 'building ScreenSnap.exe ...'
& $csc /nologo /codepage:65001 /target:exe /optimize+ /out:"$out\ScreenSnap.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $src 'ScreenSnap.cs')
if ($LASTEXITCODE -ne 0) { throw "ScreenSnap build failed ($LASTEXITCODE)" }

Write-Host 'building Panel.exe ...'
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /out:"$out\Panel.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $src 'Panel.cs')
if ($LASTEXITCODE -ne 0) { throw "Panel build failed ($LASTEXITCODE)" }

Write-Host 'building Bootstrap.exe ...'
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /out:"$out\Bootstrap.exe" (Join-Path $src 'Bootstrap.cs')
if ($LASTEXITCODE -ne 0) { throw "Bootstrap build failed ($LASTEXITCODE)" }

# Keep the binaries at medium integrity so the global hooks can see normal windows.
& icacls $out /setintegritylevel '(OI)(CI)M' /T | Out-Null

Write-Host 'done.'
