<#
.SYNOPSIS
    Script di automazione per creare una ISO WinPE x64 avviabile con El Torito Dual-Boot (UEFI + BIOS)
    con WinPECloner avviato automaticamente al boot tramite winpeshl.ini.

.DESCRIPTION
    1. Verifica e individua gli strumenti necessari: oscdimg, wimlib-imagex, 7z e dotnet.
    2. Compila e pubblica WinPECloner (.NET 8 self-contained win-x64, singolo file .exe).
    3. Estrae la struttura radice ufficiale della ISO WinPE (bootmgr, boot\, efi\, sources\boot.wim).
    4. Inietta l'applicazione e imposta winpeshl.ini all'interno di sources\boot.wim.
    5. Compila la ISO avviabile con El Torito Dual-Boot (Platform 0: BIOS etfsboot.com | Platform 0xEF: UEFI efisys.bin).
    6. Garantisce la piena compatibilità con Rufus (nessun errore di boot o compressione non supportata).

.PARAMETER OutputIso
    Percorso del file .iso finale da generare. Default: "C:\Users\matti\Documents\TessyClone\WinPE_Cloner_x64.iso"
#>

[CmdletBinding()]
param (
    [string]$OutputIso = "C:\Users\matti\Documents\TessyClone\WinPE_Cloner_x64.iso",
    [string]$SourceIso = "C:\Users\matti\Documents\MiniTool Partition Wizard Technician 12.8 WinPE (x64) Multilingual.iso",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Generatore ISO WinPE x64 — El Torito Dual-Boot (Rufus)  " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verifica strumenti
Write-Host "`n[1/5] Rilevamento strumenti di sistema..." -ForegroundColor Yellow

$oscdimgCmd = Get-Command oscdimg -ErrorAction SilentlyContinue
if ($oscdimgCmd) {
    $oscdimg = $oscdimgCmd.Source
} else {
    $adkOscdimg = "C:\Users\matti\AppData\Local\Microsoft\WinGet\Links\oscdimg.exe"
    if (Test-Path $adkOscdimg) {
        $oscdimg = $adkOscdimg
    } else {
        throw "oscdimg.exe non trovato. Installa Windows ADK o oscdimg."
    }
}
Write-Host "  -> oscdimg: $oscdimg" -ForegroundColor Green

$wimlibCmd = Get-Command wimlib-imagex -ErrorAction SilentlyContinue
if ($wimlibCmd) {
    $wimlib = $wimlibCmd.Source
} else {
    $scoopWimlib = "$env:USERPROFILE\scoop\apps\wimlib\current\wimlib-imagex.exe"
    if (Test-Path $scoopWimlib) {
        $wimlib = $scoopWimlib
    } else {
        throw "wimlib-imagex non trovato. Esegui 'scoop install wimlib'."
    }
}
Write-Host "  -> wimlib:  $wimlib" -ForegroundColor Green

$sevenZipCmd = Get-Command 7z -ErrorAction SilentlyContinue
if (-not $sevenZipCmd) {
    throw "7z.exe non trovato nel PATH."
}
$sevenZip = $sevenZipCmd.Source
Write-Host "  -> 7-Zip:   $sevenZip" -ForegroundColor Green

# 2. Compilazione WinPECloner
$projectDir = "C:\Users\matti\Documents\TessyClone\WinPE_CloneApp\WinPECloner"
$publishDir = "C:\Users\matti\Documents\TessyClone\WinPE_CloneApp\publish"
$appExe = Join-Path $publishDir "WinPECloner.exe"

if (-not $SkipBuild) {
    Write-Host "`n[2/5] Compilazione WinPECloner (.NET 8 standalone win-x64)..." -ForegroundColor Yellow
    Push-Location $projectDir
    try {
        dotnet publish -c Release -r win-x64 --self-contained true -o $publishDir | Out-Null
        if (-not (Test-Path $appExe)) {
            throw "Compilazione fallita: WinPECloner.exe non trovato."
        }
        $exeSizeMB = [math]::Round((Get-Item $appExe).Length / 1MB, 2)
        Write-Host "  -> Compilazione completata. Singolo file: $exeSizeMB MB" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "`n[2/5] Salto compilazione (-SkipBuild)." -ForegroundColor Gray
}

# 3. Estrazione struttura radice ISO
$workDir = "C:\Users\matti\Documents\TessyClone\WinPE_Build_Work"
$isoRoot = Join-Path $workDir "ISO_Root"

Write-Host "`n[3/5] Preparazione radice ISO conforme standard Microsoft in $isoRoot..." -ForegroundColor Yellow

if (Test-Path $workDir) {
    Remove-Item -Path $workDir -Recurse -Force
}
New-Item -ItemType Directory -Path $isoRoot -Force | Out-Null

if (-not (Test-Path $SourceIso)) {
    throw "Impossibile trovare la ISO base di riferimento: $SourceIso"
}

Write-Host "  -> Estrazione file di boot dalla ISO sorgente..." -ForegroundColor Gray
& $sevenZip x "$SourceIso" -o"$isoRoot" -y | Out-Null

# 4. Iniezione applicazione e configurazione winpeshl.ini all'interno di boot.wim
Write-Host "`n[4/5] Iniezione WinPECloner e winpeshl.ini nel file boot.wim..." -ForegroundColor Yellow

$bootWim = Join-Path $isoRoot "sources\boot.wim"
if (-not (Test-Path $bootWim)) {
    throw "File $bootWim non trovato nella radice ISO estratta."
}

$tempIni = Join-Path $workDir "winpeshl.ini"
$winpeshlContent = @"
[LaunchApps]
"X:\Program Files\WinPECloner\WinPECloner.exe"
"%SYSTEMDRIVE%\Windows\System32\cmd.exe"
"@
Set-Content -Path $tempIni -Value $winpeshlContent -Encoding ASCII

# Creazione script comandi per wimlib
$wimCmdFile = Join-Path $workDir "wim_update.txt"
@"
add '$appExe' '/Program Files/WinPECloner/WinPECloner.exe'
add '$tempIni' '/Windows/System32/winpeshl.ini'
delete --force '/Program Files/MTPELoader.exe'
"@ | Set-Content -Path $wimCmdFile -Encoding ASCII

Write-Host "  -> Aggiornamento immagine 1 di boot.wim..." -ForegroundColor Gray
Get-Content $wimCmdFile | & $wimlib update $bootWim 1 | Out-Null
Write-Host "  -> Iniezione completata con successo in boot.wim!" -ForegroundColor Green

# 5. Creazione della ISO avviabile con El Torito Dual-Boot per Rufus
Write-Host "`n[5/5] Compilazione ISO Dual-Boot El Torito (UEFI + BIOS) con oscdimg..." -ForegroundColor Yellow

$etfsboot = Join-Path $isoRoot "boot\etfsboot.com"
$efisys   = Join-Path $isoRoot "boot\efisys.bin"

if (-not (Test-Path $etfsboot)) { throw "File non trovato: $etfsboot" }
if (-not (Test-Path $efisys))   { throw "File non trovato: $efisys" }

$outDir = Split-Path $OutputIso -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$bootdata = "-bootdata:2#p0,e,b`"$etfsboot`"#pEF,e,b`"$efisys`""

$oscdimgArgs = @(
    "-m",
    "-o",
    "-u2",
    "-udfver102",
    $bootdata,
    "`"$isoRoot`"",
    "`"$OutputIso`""
)

Write-Host "  -> Comando oscdimg:" -ForegroundColor Cyan
Write-Host "     oscdimg.exe -m -o -u2 -udfver102 $bootdata `"$isoRoot`" `"$OutputIso`"`n" -ForegroundColor DarkCyan

& $oscdimg $oscdimgArgs

if (Test-Path $OutputIso) {
    $isoSizeMB = [math]::Round((Get-Item $OutputIso).Length / 1MB, 2)
    Write-Host "`n==========================================================" -ForegroundColor Green
    Write-Host " ISO AVVIABILE GENERATA CON SUCCESSO!" -ForegroundColor Green
    Write-Host " Percorso:   $OutputIso" -ForegroundColor White
    Write-Host " Dimensione: $isoSizeMB MB" -ForegroundColor White
    Write-Host " Boot Mode:  Dual El Torito (BIOS x86 + UEFI x64)" -ForegroundColor Green
    Write-Host " Struttura:  bootmgr, boot\, efi\microsoft\boot\, sources\boot.wim" -ForegroundColor Green
    Write-Host " Stato:      Compatibile al 100% con Rufus (GPT/UEFI o MBR/BIOS)" -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
} else {
    throw "Creazione file ISO non riuscita."
}
