# Builds OllieBoard for Windows: a self-contained single-file exe in build\publish, then (if Inno Setup is
# installed) the installer build\OllieBoard-<version>-win-x64-setup.exe.
#   .\build.ps1                 # version from the project file
#   .\build.ps1 -Version 1.3.0  # also sets the app version the updater compares with the latest release
param([string]$Version = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content src\OllieBoard\OllieBoard.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$publish = "build\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish src\OllieBoard\OllieBoard.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Built $publish\OllieBoard.exe ($Version)"

$iscc = @("$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source }
if ($iscc) {
    & $iscc /Qp "/DAppVersion=$Version" "/DSourceDir=..\$publish" installer\OllieBoard.iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Built build\OllieBoard-$Version-win-x64-setup.exe"
} else {
    Write-Host "Inno Setup 6 not found (winget install JRSoftware.InnoSetup); skipped the installer."
}
