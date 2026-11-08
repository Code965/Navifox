<#
.SYNOPSIS
  Crea una release di Navifox (setup + pacchetto di aggiornamento) con Velopack.

.EXAMPLE
  .\release.ps1 1.0.1
  .\release.ps1 1.1.0 -Notes "Nuovo Data Sync"
  .\release.ps1 1.1.0 -Destination "\\server\navifox"      # pubblica anche sulla condivisione di rete
  .\release.ps1 1.1.0 -UpdateSource "https://github.com/utente/navifox"

.DESCRIPTION
  1. dotnet publish (Release, win-x64, self-contained: non serve installare .NET sui PC)
  2. vpk pack  -> Releases\Navifox-win-Setup.exe, pacchetto completo e delta rispetto alla versione precedente
  3. opzionale: copia dei file nella -Destination (cartella condivisa da cui i client si aggiornano)

  Firma digitale (facoltativa): con -SignParams (certificato .pfx, tramite signtool) oppure -AzureTrustedSignFile
  vpk firma Navifox.exe, le librerie e il Setup: Windows mostra l'editore invece di «editore sconosciuto».
    .\release.ps1 1.1.0 -SignParams '/f firma.pfx /p password /fd sha256 /tr http://timestamp.digicert.com /td sha256'
    .\release.ps1 1.1.0 -AzureTrustedSignFile azure-signing.json
#>
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')][string]$Version,
    [string]$Notes = "",
    # Dove i client installati cercano gli aggiornamenti (incorporato nell'exe). Predefinita: la cartella releases.
    [string]$UpdateSource = "",
    # Cartella dove copiare la release pubblicata (es. \\server\navifox). Predefinita: nessuna copia.
    [string]$Destination = "",
    # Argomenti per signtool (es. '/f cert.pfx /p pwd /fd sha256 /tr http://timestamp.digicert.com /td sha256').
    [string]$SignParams = "",
    # File JSON di Azure Trusted Signing (Endpoint, CodeSigningAccountName, CertificateProfileName).
    [string]$AzureTrustedSignFile = ""
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_ROOT = Split-Path (Get-Command $dotnet).Source

$releases = [System.IO.Path]::GetFullPath((Join-Path $root '..\releases'))
if (-not $UpdateSource) { $UpdateSource = if ($Destination) { $Destination } else { $releases } }
$publish = Join-Path $root "bin\publish\$Version"

$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    Write-Host "Installo lo strumento vpk (Velopack)..." -ForegroundColor DarkGray
    & $dotnet tool install -g vpk | Out-Null
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

Write-Host "==> Compilazione Navifox $Version" -ForegroundColor DarkYellow
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish (Join-Path $root 'App\Navifox.App.csproj') -c Release -r win-x64 --self-contained true `
    -o $publish "-p:Version=$Version" "-p:NavifoxUpdateSource=$UpdateSource" "-p:BaseOutputPath=bin/NaviFoxRelease/" -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "Compilazione non riuscita" }

Write-Host "==> Pacchetto Velopack" -ForegroundColor DarkYellow
New-Item -ItemType Directory -Force $releases | Out-Null
$packArgs = @('pack', '--packId', 'Navifox', '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'Navifox.exe',
    '--packTitle', 'NaviFox', '--packAuthors', 'NaviFox', '--icon', (Join-Path $root 'Assets\NaviFoxBrand\navifox-dark.ico'),
    '--outputDir', $releases, '--runtime', 'win-x64')
if ($Notes) {
    $notesFile = Join-Path $publish 'release-notes.md'
    Set-Content -Path $notesFile -Value $Notes -Encoding utf8
    $packArgs += @('--releaseNotes', $notesFile)
}
if ($SignParams) {
    $packArgs += @('--signParams', $SignParams)
    Write-Host "    firma digitale: certificato" -ForegroundColor DarkGray
}
elseif ($AzureTrustedSignFile) {
    $packArgs += @('--azureTrustedSignFile', $AzureTrustedSignFile)
    Write-Host "    firma digitale: Azure Trusted Signing" -ForegroundColor DarkGray
}
& vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack non riuscito" }

if ($Destination) {
    Write-Host "==> Pubblicazione in $Destination" -ForegroundColor DarkYellow
    New-Item -ItemType Directory -Force $Destination | Out-Null
    Get-ChildItem $releases -File | Where-Object { $_.Name -notlike '*.json' -and $_.Name -notlike 'RELEASES*' } |
        Copy-Item -Destination $Destination -Force
    # Gli indici per ultimi, così i client non vedono una versione i cui file non sono ancora copiati.
    Get-ChildItem $releases -File | Where-Object { $_.Name -like '*.json' -or $_.Name -like 'RELEASES*' } |
        Copy-Item -Destination $Destination -Force
}

Write-Host ""
Write-Host "Navifox $Version pronto." -ForegroundColor Green
Write-Host "  Setup:         $releases\Navifox-win-Setup.exe"
Write-Host "  Aggiornamenti: i client installati cercano in $UpdateSource"
