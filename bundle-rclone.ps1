[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)

$ErrorActionPreference = 'Stop'
$version = '1.76.0'
$zipName = "rclone-v$version-windows-amd64.zip"
$zipHash = '8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('surfaceframe-rclone-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $Output 'rclone'
try {
    [IO.Directory]::CreateDirectory($temp) | Out-Null
    $archive = Join-Path $temp $zipName
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/rclone/rclone/releases/download/v$version/$zipName" -OutFile $archive -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $zipHash) {
        throw 'Il checksum del pacchetto rclone non corrisponde.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $temp
    $exe = Join-Path $temp "rclone-v$version-windows-amd64/rclone.exe"
    $checkConfig = Join-Path $temp 'verify.conf'
    $check = & $exe config create surfaceframe onedrive tenant consumers access_scopes 'Files.Read offline_access' --non-interactive --config $checkConfig | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $check.Option.Name -ne 'config_is_local') {
        throw 'Il componente rclone non ha prodotto la richiesta di accesso attesa.'
    }
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    Copy-Item -LiteralPath $exe -Destination $destination
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'rclone-LICENSE.txt') -Destination $destination
}
finally {
    if (([IO.Path]::GetDirectoryName($temp) -eq [IO.Path]::GetTempPath().TrimEnd('\')) -and
        (Test-Path -LiteralPath $temp -PathType Container)) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
