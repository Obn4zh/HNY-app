# Собирает установщик: dist\HNY-Setup.msi
# Нужны .NET SDK и WiX Toolset v3 (candle.exe / light.exe).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$wix = if ($env:WIX) { Join-Path $env:WIX 'bin' } else { 'C:\Program Files (x86)\WiX Toolset v3.14\bin' }
$obj = Join-Path $root 'obj\installer'
$dist = Join-Path $root 'dist'

if (-not (Test-Path (Join-Path $root 'assets\HNY.ico'))) { & (Join-Path $PSScriptRoot 'make-icon.ps1') }

dotnet publish (Join-Path $root 'HNY.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

New-Item -ItemType Directory -Force $obj, $dist | Out-Null
Push-Location $PSScriptRoot
try {
    & "$wix\candle.exe" -nologo -arch x64 -ext WixUtilExtension -out "$obj\HNY.wixobj" HNY.wxs
    if ($LASTEXITCODE -ne 0) { throw 'candle failed' }
    & "$wix\light.exe" -nologo -ext WixUIExtension -ext WixUtilExtension -cultures:ru-RU `
        -spdb -out "$dist\HNY-Setup.msi" "$obj\HNY.wixobj"
    if ($LASTEXITCODE -ne 0) { throw 'light failed' }
}
finally { Pop-Location }

Get-Item "$dist\HNY-Setup.msi" | ForEach-Object { '{0}  {1:N1} MB' -f $_.FullName, ($_.Length / 1MB) }
