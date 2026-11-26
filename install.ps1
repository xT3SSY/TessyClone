if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Esegui PowerShell come Amministratore!" -ForegroundColor Red
    return
}
<#
.SYNOPSIS
    Script di installazione e compilazione one-liner per TessyClone.
.DESCRIPTION
    Eseguibile tramite:
    irm https://raw.githubusercontent.com/xT3SSY/TessyClone/main/install.ps1 | iex

    1. Verifica privilegi di Amministratore e disponibilità di winget.
    2. Installa i prerequisiti mancanti via winget:
       - Git.Git
       - Microsoft.DotNet.SDK.8 (.NET 8 SDK, TargetFramework net8.0-windows)
       - Microsoft.WindowsADK (solo OptionId.DeploymentTools)
       - Microsoft.WindowsADK.WinPEAddon (solo OptionId.WindowsPreinstallationEnvironment)
    3. Clona o aggiorna il repository in $env:USERPROFILE\Documents\TessyClone.
    4. Compila TessyClone (.NET 8) e prepara ISO_Root WinPE tramite copype.cmd.
    5. Se .\Drivers\ non contiene file .inf, scarica ed estrae automaticamente i driver Intel RST/VMD (SetupRST.exe).
    6. Lancia Build-TessyClone-ISO.ps1 per generare la ISO finale.
#>

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "       TESSYCLONE - INSTALLAZIONE E BUILD AUTOMATICA      " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Verifica esplicita dei privilegi di Amministratore (utile anche con irm | iex)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Questo script richiede privilegi di Amministratore. Apri PowerShell come Amministratore e riprova."
}

function Update-SessionPath {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machinePath;$userPath"
}

function Find-AdkRoot {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit",
        "$env:ProgramFiles\Windows Kits\10\Assessment and Deployment Kit"
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) {
            return $path
        }
    }
    return $null
}

function Find-Oscdimg {
    $cmd = Get-Command "oscdimg.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $adkRoot = Find-AdkRoot
    if ($adkRoot) {
        $adkOscdimg = Join-Path $adkRoot "Deployment Tools\amd64\Oscdimg\oscdimg.exe"
        if (Test-Path $adkOscdimg) { return $adkOscdimg }
    }
    return $null
}

function Test-DotNet8SdkInstalled {
    $dotnetCmd = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) {
        $defaultDotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
        if (Test-Path $defaultDotnet) {
            $dotnetCmd = Get-Command $defaultDotnet -ErrorAction SilentlyContinue
        }
    }
    if (-not $dotnetCmd) { return $false }

    $sdks = & $dotnetCmd.Source --list-sdks 2>$null
    return ($sdks -match '^8\.')
}

function Test-WinPeAddonInstalled {
    $adkRoot = Find-AdkRoot
    if (-not $adkRoot) { return $false }

    $winpeRoot = Join-Path $adkRoot "Windows Preinstallation Environment"
    $copypeCmd = Join-Path $winpeRoot "copype.cmd"
    $winpeWim  = Join-Path $winpeRoot "amd64\en-us\winpe.wim"
    return ((Test-Path $copypeCmd) -and (Test-Path $winpeWim))
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [string]$OverrideArgs
    )

    Write-Host "  -> Installazione $DisplayName ($PackageId) tramite winget..." -ForegroundColor Yellow
    $wingetArgs = @(
        "install",
        "--id", $PackageId,
        "--exact",
        "--silent",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )
    if ($OverrideArgs) {
        $wingetArgs += @("--override", $OverrideArgs)
    }

    & winget @wingetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Installazione di $DisplayName ($PackageId) fallita (Exit code: $LASTEXITCODE)."
    }
    Update-SessionPath
    Write-Host "  [OK] $DisplayName installato." -ForegroundColor Green
}

# 1. Verifica winget
if (-not (Get-Command "winget.exe" -ErrorAction SilentlyContinue)) {
    throw "winget.exe non trovato. Installa 'App Installer' da Microsoft Store prima di procedere."
}

# 2. Controllo e installazione prerequisiti mancanti
Write-Host "`n[1/5] Verifica prerequisiti di sistema..." -ForegroundColor Yellow

# Git
if (-not (Get-Command "git.exe" -ErrorAction SilentlyContinue) -and -not (Test-Path "$env:ProgramFiles\Git\cmd\git.exe")) {
    Install-WingetPackage -PackageId "Git.Git" -DisplayName "Git"
} else {
    Write-Host "  [OK] Git già presente." -ForegroundColor Green
}

# .NET 8 SDK (corrispondente a <TargetFramework>net8.0-windows</TargetFramework>)
if (-not (Test-DotNet8SdkInstalled)) {
    Install-WingetPackage -PackageId "Microsoft.DotNet.SDK.8" -DisplayName ".NET 8 SDK"
} else {
    Write-Host "  [OK] .NET 8 SDK già presente." -ForegroundColor Green
}

# Windows ADK (solo Deployment Tools)
if (-not (Find-Oscdimg)) {
    Install-WingetPackage `
        -PackageId "Microsoft.WindowsADK" `
        -DisplayName "Windows ADK (Deployment Tools)" `
        -OverrideArgs "/quiet /norestart /ceip off /features OptionId.DeploymentTools"
} else {
    Write-Host "  [OK] Windows ADK (Deployment Tools) già presente." -ForegroundColor Green
}

# Windows ADK WinPE Add-on (solo Windows Preinstallation Environment)
if (-not (Test-WinPeAddonInstalled)) {
    Install-WingetPackage `
        -PackageId "Microsoft.WindowsADK.WinPEAddon" `
        -DisplayName "Windows ADK WinPE Add-on" `
        -OverrideArgs "/quiet /norestart /ceip off /features OptionId.WindowsPreinstallationEnvironment"
} else {
    Write-Host "  [OK] Windows ADK WinPE Add-on già presente." -ForegroundColor Green
}

Update-SessionPath
$gitExe = (Get-Command "git.exe" -ErrorAction SilentlyContinue).Source
if (-not $gitExe) { $gitExe = "$env:ProgramFiles\Git\cmd\git.exe" }
$dotnetExe = (Get-Command "dotnet.exe" -ErrorAction SilentlyContinue).Source
if (-not $dotnetExe) { $dotnetExe = "$env:ProgramFiles\dotnet\dotnet.exe" }

# 3. Clone o aggiornamento repository in $env:USERPROFILE\Documents\TessyClone
Write-Host "`n[2/5] Sincronizzazione repository TessyClone..." -ForegroundColor Yellow
$repoUrl = "https://github.com/xT3SSY/TessyClone.git"
$docsDir = Join-Path $env:USERPROFILE "Documents"
$repoDir = Join-Path $docsDir "TessyClone"

if (-not (Test-Path $docsDir)) {
    New-Item -ItemType Directory -Path $docsDir -Force | Out-Null
}

if (Test-Path (Join-Path $repoDir ".git")) {
    Write-Host "  -> Repository già presente in $repoDir. Esecuzione git pull..." -ForegroundColor Cyan
    & $gitExe -C $repoDir pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        throw "Errore durante l'aggiornamento del repository con git pull."
    }
} elseif (Test-Path $repoDir) {
    $existingItems = Get-ChildItem -Path $repoDir -Force -ErrorAction SilentlyContinue
    if ($existingItems.Count -gt 0) {
        throw "La cartella $repoDir esiste già ma non è un repository Git valido. Spostala o svuotala prima di continuare."
    }
    & $gitExe clone $repoUrl $repoDir
    if ($LASTEXITCODE -ne 0) {
        throw "Errore durante il clone del repository."
    }
} else {
    Write-Host "  -> Clonazione di $repoUrl in $repoDir..." -ForegroundColor Cyan
    & $gitExe clone $repoUrl $repoDir
    if ($LASTEXITCODE -ne 0) {
        throw "Errore durante il clone del repository."
    }
}
Write-Host "  [OK] Repository pronto in: $repoDir" -ForegroundColor Green

# 4. Compilazione .NET 8, preparazione base WinPE, driver Intel RST/VMD e avvio Build-TessyClone-ISO.ps1
Push-Location $repoDir
try {
    Write-Host "`n[3/5] Compilazione applicazione TessyClone (.NET 8 win-x64)..." -ForegroundColor Yellow
    $csprojPath = Join-Path $repoDir "WinPE_CloneApp\WinPECloner\WinPECloner.csproj"
    $publishDir = Join-Path $repoDir "WinPE_CloneApp\publish"
    & $dotnetExe publish $csprojPath -c Release -r win-x64 -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Compilazione .NET fallita (Exit code: $LASTEXITCODE)."
    }
    Write-Host "  [OK] Compilazione completata in $publishDir." -ForegroundColor Green

    # Prepara WinPE_Build_Work\ISO_Root con copype.cmd se boot.wim o i file di boot mancano
    $isoRootDir = Join-Path $repoDir "WinPE_Build_Work\ISO_Root"
    $bootWim    = Join-Path $isoRootDir "sources\boot.wim"
    $etfsboot   = Join-Path $isoRootDir "boot\etfsboot.com"
    $efisys     = Join-Path $isoRootDir "boot\efisys.bin"

    if (-not (Test-Path $bootWim) -or -not (Test-Path $etfsboot) -or -not (Test-Path $efisys)) {
        Write-Host "`n[3b/5] Inizializzazione immagine base WinPE tramite ADK (copype amd64)..." -ForegroundColor Yellow
        $adkRoot = Find-AdkRoot
        if (-not $adkRoot) {
            throw "Windows ADK non trovato dopo l'installazione."
        }
        $dandISetEnv = Join-Path $adkRoot "Deployment Tools\DandISetEnv.bat"
        $copypeCmd   = Join-Path $adkRoot "Windows Preinstallation Environment\copype.cmd"
        if (-not (Test-Path $dandISetEnv) -or -not (Test-Path $copypeCmd)) {
            throw "Strumenti ADK (DandISetEnv.bat / copype.cmd) non trovati in $adkRoot."
        }

        $copypeStage = Join-Path $repoDir "WinPE_Build_Work\Copype_Stage"
        if (Test-Path $copypeStage) {
            Remove-Item -Path $copypeStage -Recurse -Force
        }
        $workParent = Split-Path $copypeStage -Parent
        if (-not (Test-Path $workParent)) {
            New-Item -ItemType Directory -Path $workParent -Force | Out-Null
        }

        & cmd.exe /c "call `"$dandISetEnv`" >nul && call `"$copypeCmd`" amd64 `"$copypeStage`""
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $copypeStage "media\sources\boot.wim"))) {
            throw "Errore durante l'esecuzione di copype.cmd amd64."
        }

        if (Test-Path $isoRootDir) {
            Remove-Item -Path $isoRootDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $isoRootDir -Force | Out-Null
        Copy-Item -Path (Join-Path $copypeStage "media\*") -Destination $isoRootDir -Recurse -Force
        Copy-Item -Path (Join-Path $copypeStage "fwfiles\etfsboot.com") -Destination (Join-Path $isoRootDir "boot\etfsboot.com") -Force
        Copy-Item -Path (Join-Path $copypeStage "fwfiles\efisys.bin") -Destination (Join-Path $isoRootDir "boot\efisys.bin") -Force
        Remove-Item -Path $copypeStage -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  [OK] Struttura base WinPE pronta in $isoRootDir." -ForegroundColor Green
    }

    # Scaricamento ed estrazione automatica driver Intel RST/VMD se .\Drivers\ non contiene file .inf
    Write-Host "`n[4/5] Verifica driver Intel RST/VMD in .\Drivers\..." -ForegroundColor Yellow
    $driversDir = Join-Path $PWD "Drivers"
    if (-not (Test-Path $driversDir)) {
        New-Item -ItemType Directory -Path $driversDir -Force | Out-Null
    }

    $infFiles = Get-ChildItem -Path $driversDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
    if ($infFiles.Count -eq 0) {
        $rstUrl = "https://downloadmirror.intel.com/872385/SetupRST.exe"
        $setupRstPath = Join-Path $env:TEMP "SetupRST.exe"

        try {
            Write-Host "  -> Nessun file .inf trovato. Scaricamento automatico Intel RST/VMD ($rstUrl)..." -ForegroundColor Cyan
            if (Get-Command "curl.exe" -ErrorAction SilentlyContinue) {
                & curl.exe -fL -A "Mozilla/5.0" -o "$setupRstPath" "$rstUrl"
                if ($LASTEXITCODE -ne 0) {
                    throw "curl.exe ha restituito codice di uscita $LASTEXITCODE durante il download di SetupRST.exe."
                }
            } else {
                Invoke-WebRequest -Uri $rstUrl -OutFile $setupRstPath -Headers @{ "User-Agent" = "Mozilla/5.0" } -UseBasicParsing
            }

            if (-not (Test-Path $setupRstPath) -or (Get-Item $setupRstPath).Length -le 0) {
                throw "Download di SetupRST.exe non riuscito o file vuoto."
            }

            Write-Host "  -> Estrazione driver Intel RST/VMD in $driversDir..." -ForegroundColor Cyan
            $proc = Start-Process -FilePath "$env:TEMP\SetupRST.exe" -ArgumentList "-extractdrivers `"$PWD\Drivers`"" -Wait -NoNewWindow -PassThru
            if ($proc.ExitCode -ne 0) {
                throw "Estrazione driver con SetupRST.exe fallita (Exit code: $($proc.ExitCode))."
            }

            $extractedInf = Get-ChildItem -Path $driversDir -Filter "*.inf" -Recurse -File -ErrorAction SilentlyContinue
            Write-Host "  [OK] Estratti $($extractedInf.Count) file .inf in $driversDir." -ForegroundColor Green
        }
        finally {
            if (Test-Path $setupRstPath) {
                Remove-Item -Path $setupRstPath -Force -ErrorAction SilentlyContinue
            }
        }
    } else {
        Write-Host "  [OK] Trovati $($infFiles.Count) file .inf già presenti in $driversDir." -ForegroundColor Green
    }

    Write-Host "`n[5/5] Avvio Build-TessyClone-ISO.ps1..." -ForegroundColor Yellow
    $buildScript = Join-Path $repoDir "Build-TessyClone-ISO.ps1"
    $oscdimgExe  = Find-Oscdimg
    if ($oscdimgExe) {
        & $buildScript -OscdimgExe $oscdimgExe
    } else {
        & $buildScript
    }
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Build-TessyClone-ISO.ps1 terminato con errore (Exit code: $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}
