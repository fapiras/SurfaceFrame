[CmdletBinding()]
param(

    [Parameter(Mandatory)][string]$Tag
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$project = [xml](Get-Content -LiteralPath 'src/SurfaceFrame/SurfaceFrame.csproj' -Raw)
$version = $project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $Tag -cne "v$version") {
    throw "Il tag deve coincidere con la versione del progetto (v$version)."
}

$compiled = 'artifacts/win-x64'
$exe = Get-Item -LiteralPath (Join-Path $compiled 'SurfaceFrame.exe')
$binary = $exe.VersionInfo
if ("$($binary.FileMajorPart).$($binary.FileMinorPart).$($binary.FileBuildPart)" -cne $version) {
    throw 'Eseguibile compilato con una versione diversa dal tag.'
}
if (!(Test-Path -LiteralPath (Join-Path $compiled 'coreclr.dll'))) { throw 'Il pacchetto deve includere il runtime .NET.' }

$release = 'artifacts/release'
$setup = 'artifacts/release-setup'
New-Item -ItemType Directory -Path $release, $setup | Out-Null
$zip = Join-Path $release 'SurfaceFrame-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path -LiteralPath $compiled).Path,
    (Join-Path $PSScriptRoot $zip), [IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item -LiteralPath $compiled -Destination (Join-Path $setup 'app') -Recurse
Copy-Item -LiteralPath 'Avvia-Surface-Frame.cmd', 'setup.ps1', 'README.md', 'settings.example.json' -Destination $setup
$setupPath = Join-Path $PSScriptRoot "$release/SurfaceFrame-Setup-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path -LiteralPath $setup).Path,
    $setupPath, [IO.Compression.CompressionLevel]::Optimal, $false)
