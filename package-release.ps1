[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Repository
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$project = [xml](Get-Content -LiteralPath 'src/SurfaceFrame/SurfaceFrame.csproj' -Raw)
$version = $project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $Tag -cne "v$version") {
    throw "Il tag deve coincidere con la versione del progetto (v$version)."
}
if ($Repository -notmatch '\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z') { throw 'Repository GitHub non valido.' }

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
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = @{ version = $version; sha256 = $hash } | ConvertTo-Json -Compress
$manifestBytes = [Text.Encoding]::UTF8.GetBytes($manifest)

if (!$env:SURFACEFRAME_SIGNING_KEY_PEM -or !$env:SURFACEFRAME_PUBLIC_KEY_PEM) {
    throw 'Configura chiave di firma e chiave pubblica prima di pubblicare.'
}
Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;

public static class SurfaceFrameReleaseSigner
{
    public static (byte[] Signature, string PublicKey) Sign(byte[] manifest, string privatePem, string publicPem)
    {
        using var signer = RSA.Create();
        using var verifier = RSA.Create();
        signer.ImportFromPem(privatePem);
        verifier.ImportFromPem(publicPem);
        if (signer.KeySize < 2048)
            throw new InvalidOperationException("Usa una chiave RSA di almeno 2048 bit.");
        byte[] signature = signer.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!verifier.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("Le chiavi RSA non corrispondono.");
        return (signature, verifier.ExportSubjectPublicKeyInfoPem());
    }
}
'@
$signed = [SurfaceFrameReleaseSigner]::Sign($manifestBytes, $env:SURFACEFRAME_SIGNING_KEY_PEM, $env:SURFACEFRAME_PUBLIC_KEY_PEM)
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "$release/surfaceframe-update.json"), $manifestBytes)
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "$release/surfaceframe-update.sig"), $signed.Item1)

Copy-Item -LiteralPath $compiled -Destination (Join-Path $setup 'app') -Recurse
Copy-Item -LiteralPath 'Avvia-Surface-Frame.cmd', 'setup.ps1', 'README.md', 'settings.example.json' -Destination $setup
$channel = @{ repository = $Repository; publicKeyPem = $signed.Item2 } | ConvertTo-Json -Compress
Set-Content -LiteralPath (Join-Path $setup 'update-channel.json') -Value $channel -Encoding utf8NoBOM
$setupPath = Join-Path $PSScriptRoot "$release/SurfaceFrame-Setup-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path -LiteralPath $setup).Path,
    $setupPath, [IO.Compression.CompressionLevel]::Optimal, $false)
