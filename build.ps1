[CmdletBinding()]
param([switch]$FrameworkDependent)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$app = 'src/SurfaceFrame/SurfaceFrame.csproj'
$checks = 'tests/SurfaceFrame.Checks/SurfaceFrame.Checks.csproj'
$output = Join-Path $PSScriptRoot 'artifacts/win-x64'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

dotnet --version
if ($LASTEXITCODE -ne 0) { throw 'Installa il kit di sviluppo Microsoft .NET 10 x64.' }

dotnet run --project $checks --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'La verifica o la compilazione non è riuscita.' }

dotnet publish $app --configuration Release --runtime win-x64 --self-contained $selfContained --output $output
if ($LASTEXITCODE -ne 0) { throw 'La pubblicazione Windows x64 non è riuscita.' }

& (Join-Path $PSScriptRoot 'bundle-rclone.ps1') -Output $output

Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File | Remove-Item

if (-not (Test-Path -LiteralPath (Join-Path $output 'SurfaceFrame.exe') -PathType Leaf)) {
    throw 'Eseguibile x64 assente dalla cartella di pubblicazione.'
}
Write-Output "Eseguibile: $(Join-Path $output 'SurfaceFrame.exe')"
