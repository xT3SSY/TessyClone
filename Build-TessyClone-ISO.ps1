# ==============================================================================
# Script di Assemblaggio WinPE e Generazione ISO Bootable: TessyClone
# Con Iniezione Automatica Driver Storage (iaStorVD, iaStorAC, iaAHCIC)
# ==============================================================================

[CmdletBinding()]
param(
    [string]$IsoRootDir,
    [string]$MountDir,
    [string]$DriversDir,
    [string]$AppPublishDir,
    [string]$AssetsDir,
    [string]$OutputIso,
    [string]$OscdimgExe = "C:\Users\matti\AppData\Local\Microsoft\WinGet\Links\oscdimg.exe",
    [string]$WimLibExe = "C:\Users\matti\scoop\apps\wimlib\current\wimlib-imagex.exe"
)

if (-not $PSScriptRoot) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $PSScriptRoot) {
    $PSScriptRoot = (Get-Location).Path
}

if (-not $IsoRootDir) { $IsoRootDir = Join-Path $PSScriptRoot "WinPE_Build_Work\ISO_Root" }
if (-not $MountDir) { $MountDir = Join-Path $PSScriptRoot "WinPE_Build_Work\Mount" }
if (-not $DriversDir) { $DriversDir = Join-Path $PSScriptRoot "Drivers" }
if (-not $AppPublishDir) { $AppPublishDir = Join-Path $PSScriptRoot "WinPE_CloneApp\publish" }
if (-not $AssetsDir) { $AssetsDir = Join-Path $PSScriptRoot "assets" }
if (-not $OutputIso) { $OutputIso = Join-Path $PSScriptRoot "WinPE_Cloner_x64.iso" }

# Controllo privilegi di amministratore (DISM richiede elevazione)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "==========================================================" -ForegroundColor Yellow
    Write-Host "[INFO] Elevazione permessi di amministratore richiesta per DISM..." -ForegroundColor Yellow
    Write-Host "==========================================================" -ForegroundColor Yellow
    
    $tempBat = Join-Path $PSScriptRoot "elev_build_runner.bat"
    $tempLog = Join-Path $PSScriptRoot "elev_build.log"
    $cmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" %* > `"$tempLog`" 2>&1"
    Set-Content -Path $tempBat -Value $cmd -Encoding Ascii
    
    $proc = Start-Process cmd.exe -ArgumentList "/c `"$tempBat`"" -Verb RunAs -Wait -PassThru
    if (Test-Path $tempLog) {
        Get-Content $tempLog
        Remove-Item $tempLog -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $tempBat -Force -ErrorAction SilentlyContinue
    exit $proc.ExitCode
}

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "       AVVIO PROCEDURA BUILD ISO WINPE TESSYCLONE         " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verifica prerequisiti e percorsi
if (-not (Test-Path $OscdimgExe)) {
    $foundOscd = Get-Command oscdimg.exe -ErrorAction SilentlyContinue
    if ($foundOscd) {
        $OscdimgExe = $foundOscd.Source
    } else {
        throw "oscdimg.exe non trovato in: $OscdimgExe"
    }
}

$appExe = Join-Path $AppPublishDir "TessyClone.exe"
if (-not (Test-Path $appExe)) {
    throw "Eseguibile TessyClone non trovato in: $appExe. Eseguire prima dotnet publish."
}

$bootWim = Join-Path $IsoRootDir "sources\boot.wim"
if (-not (Test-Path $bootWim)) {
    throw "boot.wim non trovato in: $bootWim"
}

if (-not (Test-Path $MountDir)) {
    New-Item -ItemType Directory -Path $MountDir -Force | Out-Null
}

# 2. Pulizia di eventuali mount precedenti non chiusi
Write-Host "[1/5] Controllo stato immagini montate con DISM..." -ForegroundColor Yellow
$mountedInfo = & dism /Get-MountedWimInfo 2>&1 | Out-String
if ($mountedInfo -match [regex]::Escape($MountDir)) {
    Write-Host "  Rilevata immagine precedentemente montata in $MountDir. Esecuzione smontaggio con scarto modifiche..." -ForegroundColor Yellow
    & dism /Unmount-Wim /MountDir:"$MountDir" /Discard
}

# 3. Montaggio boot.wim con DISM
Write-Host "[2/5] Montaggio boot.wim in $MountDir..." -ForegroundColor Yellow
& dism /Mount-Wim /WimFile:"$bootWim" /Index:1 /MountDir:"$MountDir"
if ($LASTEXITCODE -ne 0) {
    throw "Errore durante il montaggio di boot.wim con DISM (Exit code: $LASTEXITCODE)"
}

$mountCommitted = $false
try {
    # 4. Iniezione applicazione, configurazione shell e sfondi
    Write-Host "[3/5] Iniezione eseguibile TessyClone, configurazione shell e sfondi..." -ForegroundColor Yellow
    
    # Rimuovi eventuale vecchia cartella WinPECloner se presente
    $oldClonerDir = Join-Path $MountDir "Program Files\WinPECloner"
    if (Test-Path $oldClonerDir) {
        Remove-Item -Recurse -Force $oldClonerDir -ErrorAction SilentlyContinue
    }
    
    # Crea cartella Program Files\TessyClone
    $destAppDir = Join-Path $MountDir "Program Files\TessyClone"
    if (-not (Test-Path $destAppDir)) {
        New-Item -ItemType Directory -Path $destAppDir -Force | Out-Null
    }
    Copy-Item -Path $appExe -Destination (Join-Path $destAppDir "TessyClone.exe") -Force
    
    # Genera ed inietta winpeshl.ini
    $winpeshlContent = @"
[LaunchApps]
"X:\Program Files\TessyClone\TessyClone.exe"
"@
    $destSys32 = Join-Path $MountDir "Windows\System32"
    $winpeshlSys32 = Join-Path $destSys32 "winpeshl.ini"
    $winpeshlProg = Join-Path $MountDir "Program Files\Winpeshl.ini"
    Set-Content -Path $winpeshlSys32 -Value $winpeshlContent -Encoding Ascii
    Set-Content -Path $winpeshlProg -Value $winpeshlContent -Encoding Ascii
    
    # Copia sfondi (winpe.jpg, winpe.bmp, setup.bmp, setup.jpg, winre.jpg)
    $wallpapers = @("winpe.jpg", "winpe.bmp", "setup.bmp", "setup.jpg", "winre.jpg")
    foreach ($wp in $wallpapers) {
        $srcWp = Join-Path $AssetsDir $wp
        if (Test-Path $srcWp) {
            Copy-Item -Path $srcWp -Destination (Join-Path $destSys32 $wp) -Force
            Copy-Item -Path $srcWp -Destination (Join-Path $MountDir "Program Files\$wp") -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "  [OK] TessyClone.exe, winpeshl.ini e asset grafici integrati con successo." -ForegroundColor Green
    
    # 5. Iniezione ricorsiva dei driver da .\Drivers
    Write-Host "[4/5] Iniezione driver di archiviazione da cartella Drivers..." -ForegroundColor Yellow
    $infFiles = @()
    if (Test-Path $DriversDir) {
        $infFiles = Get-ChildItem -Path $DriversDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
    }
    
    if ($infFiles.Count -gt 0) {
        Write-Host "  Trovati $($infFiles.Count) driver .inf in $DriversDir. Avvio dism /Add-Driver ricorsivo..." -ForegroundColor Cyan
        & dism /Image:"$MountDir" /Add-Driver /Driver:"$DriversDir" /Recurse /ForceUnsigned
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "DISM ha restituito codice $LASTEXITCODE durante l'iniezione dei driver (alcuni potrebbero essere già presenti o incompatibili). Il processo prosegue regolarmente."
        } else {
            Write-Host "  [OK] Iniezione driver completata con successo." -ForegroundColor Green
        }
    } else {
        Write-Host "  Nessun driver .inf trovato in $DriversDir." -ForegroundColor Gray
    }
    
    # 6. Verifica a schermo dei driver di terze parti integrati
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host "       ELENCO DRIVER DI TERZE PARTI INTEGRATI IN BOOT.WIM " -ForegroundColor DarkCyan
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkCyan
    & dism /Image:"$MountDir" /Get-Drivers
    Write-Host "----------------------------------------------------------" -ForegroundColor DarkCyan
    
    # 7. Smontaggio e commit di boot.wim
    Write-Host "[5/5] Salvataggio modifiche e smontaggio boot.wim (/Commit)..." -ForegroundColor Yellow
    & dism /Unmount-Wim /MountDir:"$MountDir" /Commit
    if ($LASTEXITCODE -ne 0) {
        throw "Errore durante lo smontaggio e salvataggio di boot.wim con DISM (Exit code: $LASTEXITCODE)"
    }
    $mountCommitted = $true
    Write-Host "  [OK] boot.wim salvato e smontato con successo." -ForegroundColor Green
}
finally {
    if (-not $mountCommitted) {
        Write-Warning "Rilevata interruzione o errore. Tentativo di smontaggio con /Discard di $MountDir..."
        & dism /Unmount-Wim /MountDir:"$MountDir" /Discard
    }
}

# 8. Ottimizzazione BCD (quietboot yes, bootux disabled, personalizzazione)
Write-Host "Configurazione BCD (quietboot, bootux, branding)..." -ForegroundColor Yellow
$bootBcd = Join-Path $IsoRootDir "boot\bcd"
$efiBcd  = Join-Path $IsoRootDir "efi\microsoft\boot\bcd"

$batFile = Join-Path $PSScriptRoot "apply_bcd.bat"
$batContent = @"
@echo off
bcdedit /store "$bootBcd" /set {bootmgr} displaybootmenu no
bcdedit /store "$bootBcd" /set {default} quietboot yes
bcdedit /store "$bootBcd" /set {default} highestmode yes
bcdedit /store "$bootBcd" /set {default} bootux disabled
bcdedit /store "$bootBcd" /set {default} description "TessyClone"

bcdedit /store "$efiBcd" /set {bootmgr} displaybootmenu no
bcdedit /store "$efiBcd" /set {default} quietboot yes
bcdedit /store "$efiBcd" /set {default} highestmode yes
bcdedit /store "$efiBcd" /set {default} bootux disabled
bcdedit /store "$efiBcd" /set {default} description "TessyClone"
"@
$batContent | Set-Content -Path $batFile -Encoding Ascii
& cmd.exe /c "`"$batFile`""
Remove-Item -Path $batFile -Force -ErrorAction SilentlyContinue
Write-Host "[OK] File BCD aggiornati (BIOS e UEFI)" -ForegroundColor Green

# 9. Compilazione ISO El Torito con oscdimg
Write-Host "Compilazione file .ISO avviabile per Rufus/Ventoy con oscdimg..." -ForegroundColor Yellow
$etfsboot = Join-Path $IsoRootDir "boot\etfsboot.com"
$efisys   = Join-Path $IsoRootDir "boot\efisys.bin"

if (-not (Test-Path $etfsboot)) { throw "etfsboot.com non trovato in: $etfsboot" }
if (-not (Test-Path $efisys)) { throw "efisys.bin non trovato in: $efisys" }

$oscdimgArgs = @(
    "-m",
    "-o",
    "-u2",
    "-udfver102",
    "-bootdata:2#p0,e,b`"$etfsboot`"#pEF,e,b`"$efisys`"",
    "`"$IsoRootDir`"",
    "`"$OutputIso`""
)

& $OscdimgExe $oscdimgArgs
if ($LASTEXITCODE -ne 0) {
    throw "Errore durante la generazione della ISO con oscdimg (Exit code: $LASTEXITCODE)"
}

Write-Host "[OK] ISO generata con successo: $OutputIso" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "                  BUILD COMPLETATA CON SUCCESSO           " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
