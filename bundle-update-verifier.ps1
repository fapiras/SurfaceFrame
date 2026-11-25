[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)

$ErrorActionPreference = 'Stop'
$version = '2.105.0'
$zipName = "gh_${version}_windows_amd64.zip"
$sha256 = '8813a72c899900afaa10e3fec8fbc64b908c89dbfe837e95ed2fa500fc140627'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('surfaceframe-verifier-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temp | Out-Null
    $archive = Join-Path $temp $zipName
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/cli/cli/releases/download/v$version/$zipName" -OutFile $archive -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) {
        throw 'Il checksum del verificatore GitHub non corrisponde.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $temp
    $exe = Get-ChildItem -LiteralPath $temp -Filter 'gh.exe' -File -Recurse | Select-Object -First 1
    $destination = Join-Path $Output 'update-tools'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $exe.FullName -Destination $destination
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'github-cli-LICENSE.txt') -Destination $destination
}
finally {
    if (([IO.Path]::GetDirectoryName($temp) -eq [IO.Path]::GetTempPath().TrimEnd('\')) -and
        (Test-Path -LiteralPath $temp -PathType Container)) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
