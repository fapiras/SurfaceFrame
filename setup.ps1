$ErrorActionPreference = 'Stop'

function Test-AppFiles($directory) {
    foreach ($file in @('SurfaceFrame.exe', 'SurfaceFrame.dll', 'coreclr.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $directory $file) -PathType Leaf)) { return $false }
    }
    return $true
}

function Get-Dotnet($stage) {
    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    $executables = @($command.Source, (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    foreach ($exe in ($executables | Where-Object { $_ } | Select-Object -Unique)) {
        if ((Test-Path -LiteralPath $exe) -and ((& $exe --list-sdks) -match '^10\.0\.[0-9]+ \[')) { return $exe }
    }

    Write-Host 'Scarico il kit .NET 10 x64 temporaneo...'
    $catalog = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 45
    $version = $catalog.'latest-sdk'
    if ($version -notmatch '^10\.0\.[0-9]+$') { throw 'Versione .NET 10 non valida nei metadati Microsoft.' }
    $sdks = foreach ($release in $catalog.releases) { $release.sdk; $release.sdks }
    $sdk = $sdks | Where-Object version -EQ $version | Select-Object -First 1
    $file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-sdk-win-x64.zip' } | Select-Object -First 1
    $expectedUrl = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$version/dotnet-sdk-$version-win-x64.zip"
    if (!$file -or $file.hash -notmatch '^[a-fA-F0-9]{128}$' -or $file.url -ne $expectedUrl) {
        throw 'Il kit .NET x64 non risulta nei metadati Microsoft.'
    }
    $archive = Join-Path $stage 'dotnet.zip'
    $sdkFolder = Join-Path $stage 'sdk'
    Invoke-WebRequest -Uri $expectedUrl -UseBasicParsing -OutFile $archive -TimeoutSec 900
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) { throw 'Controllo SHA-512 del kit .NET fallito.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkFolder
    Remove-Item -LiteralPath $archive
    return (Join-Path $sdkFolder 'dotnet.exe')
}

function Test-WebView {
    $id = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach ($key in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$id",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id", "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$id")) {
        if ((Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv -match '^[1-9][0-9]*(?:\.[0-9]+){3}$') {
            return $true
        }
    }
    return $false
}

function Install-WebView($stage) {
    if (Test-WebView) { return }
    Write-Host 'Installo Microsoft Edge WebView2 Runtime...'
    $installer = Join-Path $stage 'MicrosoftEdgeWebview2Setup.exe'
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -UseBasicParsing -OutFile $installer -TimeoutSec 90
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
        throw 'La firma Microsoft del programma di installazione WebView2 non risulta valida.'
    }
    $process = Start-Process -FilePath $installer -ArgumentList '/silent', '/install' -WindowStyle Hidden -PassThru -Wait
    if (!(Test-WebView)) { throw "Runtime WebView2 non rilevato dopo l'installazione. Codice $($process.ExitCode)." }
}

$root = Join-Path $env:LOCALAPPDATA 'SurfaceFrame'
$versions = Join-Path $root 'app'
$bundled = Join-Path $PSScriptRoot 'app'
$project = Join-Path $PSScriptRoot 'src\SurfaceFrame\SurfaceFrame.csproj'
$stage = Join-Path $root ('setup-' + [Guid]::NewGuid().ToString('N'))
$log = Join-Path $root 'installazione.log'
$lock = $null
$logging = $false
$result = 0
try {
    if (![Environment]::Is64BitOperatingSystem) { throw 'Surface Frame richiede Windows x64.' }
    if (Test-AppFiles $bundled) {
        $binary = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $bundled 'SurfaceFrame.exe'))
        $version = "$($binary.FileMajorPart).$($binary.FileMinorPart).$($binary.FileBuildPart)"
    } elseif (Test-Path -LiteralPath $project -PathType Leaf) {
        $projectXml = [xml](Get-Content -LiteralPath $project -Raw)
        $version = $projectXml.Project.PropertyGroup.Version
    } else {
        throw 'Estrai tutto lo ZIP prima di avviare il launcher.'
    }
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versione del pacchetto non valida.' }
    $installed = Join-Path $versions $version
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    [IO.Directory]::CreateDirectory($versions) | Out-Null
    $lock = [IO.File]::Open((Join-Path $root 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    Start-Transcript -LiteralPath $log | Out-Null
    $logging = $true
    $firstLaunch = !(Test-AppFiles $installed)
    if ($firstLaunch -or !(Test-WebView)) {
        [IO.Directory]::CreateDirectory($stage) | Out-Null
        Install-WebView $stage
    }
    if ($firstLaunch) {
        $output = Join-Path $stage 'app'
        if (Test-AppFiles $bundled) {
            Copy-Item -LiteralPath $bundled -Destination $output -Recurse
        } else {
            $dotnet = Get-Dotnet $stage
            $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            $env:DOTNET_CLI_HOME = Join-Path $stage 'dotnet-home'
            $env:NUGET_PACKAGES = Join-Path $stage 'nuget'
            Set-Location -LiteralPath $PSScriptRoot
            Write-Host 'Preparo Surface Frame per Windows x64...'
            & $dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $output
            if ($LASTEXITCODE -ne 0) { throw "Compilazione interrotta. Codice $LASTEXITCODE." }
        }
        if (!(Test-AppFiles $output)) { throw 'La preparazione non ha prodotto tutti i file richiesti.' }
        if (Test-Path -LiteralPath $installed) {
            Rename-Item -LiteralPath $installed -NewName ($version + '-incompleta-' + [Guid]::NewGuid().ToString('N'))
        }
        $resolved = (Resolve-Path -LiteralPath $output).Path
        if ($resolved -ne (Join-Path (Resolve-Path -LiteralPath $stage).Path 'app') -or
            $installed -ne (Join-Path (Resolve-Path -LiteralPath $versions).Path $version)) {
            throw 'Percorso di preparazione inatteso.'
        }
        Move-Item -LiteralPath $resolved -Destination $installed
    }
    $channel = Join-Path $PSScriptRoot 'update-channel.json'
    $installedChannel = Join-Path $installed 'update-channel.json'
    if ((Test-Path -LiteralPath $channel -PathType Leaf) -and !(Test-Path -LiteralPath $installedChannel)) {
        Copy-Item -LiteralPath $channel -Destination $installedChannel
    }
    $exe = Join-Path $installed 'SurfaceFrame.exe'
    $settingsFile = Join-Path $root 'settings.json'
    if (Test-Path -LiteralPath $settingsFile -PathType Leaf) {
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($settings.autoStart -eq $true) {
            $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            New-ItemProperty -LiteralPath $runKey -Name SurfaceFrame -Value ('"{0}" --autostart' -f $exe) -PropertyType String -Force | Out-Null
        }
    }
    try {
        $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path $programs 'Surface Frame.lnk'))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $installed
        $shortcut.Save()
    } catch {
        Write-Warning "Collegamento Start non creato. App disponibile in $installed"
    }
    if ($firstLaunch -and !(Test-Path -LiteralPath (Join-Path $root 'settings.json'))) {
        Start-Process -FilePath $exe -WorkingDirectory $installed -ArgumentList '--setup'
    } else {
        Start-Process -FilePath $exe -WorkingDirectory $installed
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Registro: $log"
    $result = 1
} finally {
    if ($logging) { Stop-Transcript | Out-Null }
    if (Test-Path -LiteralPath $stage -PathType Container) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ((Split-Path -Parent $resolvedStage) -eq (Resolve-Path -LiteralPath $root).Path -and
            (Split-Path -Leaf $resolvedStage) -match '^setup-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if ($lock) { $lock.Dispose() }
}
exit $result
