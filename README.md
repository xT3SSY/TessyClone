# TessyClone

**TessyClone** è un'utility di clonazione dischi raw ad alte prestazioni per ambiente **Windows PE (WinPE x64)**, sviluppata in **C# / .NET 8 (Windows Forms)**. È progettata per duplicare rapidamente un disco master (1-a-N) su uno o più dischi di destinazione, automatizzando le operazioni post-clonazione e la preparazione dell'immagine ISO avviabile con supporto BIOS/UEFI e driver Intel RST/VMD.

## Funzioni Principali

- **Clonazione 1-a-N ad alte prestazioni**: copia raw Direct I/O con buffer allineati da 512 KB, disponibile in modalità **sequenziale** (un disco alla volta) o **parallela** (fino a 2 dischi simultaneamente).
- **TRIM preventivo e Zero-Skip**: esegue un comando TRIM sull'intero disco di destinazione prima della scrittura e salta automaticamente i blocchi composti esclusivamente da zeri (rilevamento SIMD), riducendo i tempi di clonazione e l'usura degli SSD.
- **Pulizia dischi (Wipe) e gestione GPT**: permette di pulire rapidamente i dischi di destinazione o convertirli in formato GPT direttamente dall'interfaccia grafica.
- **Espansione automatica delle partizioni**: se il disco di destinazione ha una capacità superiore al disco sorgente (di almeno 1 GB), la partizione principale viene estesa automaticamente per sfruttare tutto lo spazio disponibile.
- **Rigenerazione BCD e firma disco**: aggiorna la firma del disco e rigenera il Boot Configuration Data (BCD) post-clonazione per garantire il corretto avvio del sistema operativo clonato.
- **Spegnimento automatico**: opzione integrata per spegnere il PC al termine della procedura (`wpeutil shutdown` con fallback `ExitWindowsEx`).

## Struttura del Repository

```text
TessyClone/
├── assets/                     # Sfondi personalizzati per WinPE (winpe.jpg/bmp, setup.jpg/bmp, winre.jpg)
├── Drivers/                    # Cartella locale per driver Intel RST/VMD (esclusa da Git)
├── WinPE_CloneApp/
│   └── WinPECloner/            # Codice sorgente C# (.NET 8 Windows Forms)
├── Build-TessyClone-ISO.ps1    # Script PowerShell principale per assemblare boot.wim e la ISO
├── winpeshl.ini                # Configurazione avvio automatico di TessyClone.exe in WinPE
├── .gitignore
├── LICENSE
└── README.md
```

## Installazione rapida su un altro PC

Per configurare e compilare **TessyClone** da zero su un nuovo PC Windows in modo completamente automatico, apri **PowerShell come Amministratore** ed esegui:

```powershell
irm https://raw.githubusercontent.com/xT3SSY/TessyClone/main/install.ps1 | iex
```

Lo script `install.ps1` installa automaticamente tramite `winget` i prerequisiti mancanti (Git, .NET 8 SDK, Windows ADK Deployment Tools e WinPE Add-on), clona il repository in `%USERPROFILE%\Documents\TessyClone`, scarica e integra automaticamente nella ISO anche i driver **Intel RST/VMD** e genera il file finale `WinPE_Cloner_x64.iso` senza alcun passaggio manuale.

## Prerequisiti per la Build

Per compilare l'applicazione e generare l'immagine ISO avviabile sono necessari:

1. **Windows ADK + Windows PE Add-on**: fornisce l'ambiente base WinPE (`boot.wim`, `etfsboot.com`, `efisys.bin`, `oscdimg.exe`), oltre agli strumenti di sistema `DISM` e `bcdedit`.
2. **.NET 8 SDK** (`net8.0-windows`, runtime `win-x64`): necessario per compilare e pubblicare `WinPECloner.csproj` come eseguibile self-contained single-file.
3. **Driver Intel RST / VMD** *(opzionale ma consigliato per controller moderni)*: inserire i pacchetti driver estratti (file `.inf`, `.sys`, `.cat`, ecc., ad esempio `iaStorVD.inf`, `iaStorAC.inf`, `iaAHCIC.inf`) all'interno della cartella `Drivers/` (anche organizzati in sottocartelle come `Drivers/VMD` e `Drivers/NonVMD`). I driver vengono esclusi dal repository tramite `.gitignore`.

## Come Compilare e Generare la ISO

### 1. Compilare l'applicazione TessyClone

Pubblicare il progetto .NET 8 come eseguibile singolo standalone (`TessyClone.exe`) nella cartella `WinPE_CloneApp\publish`:

```powershell
dotnet publish .\WinPE_CloneApp\WinPECloner\WinPECloner.csproj -c Release -r win-x64 -o .\WinPE_CloneApp\publish
```

### 2. Preparare la directory base WinPE

Assicurarsi che `WinPE_Build_Work\ISO_Root` contenga i file base estratti dal Windows PE Add-on (inclusi `sources\boot.wim`, `boot\etfsboot.com`, `boot\efisys.bin`, `boot\bcd` ed `efi\microsoft\boot\bcd`).

### 3. Eseguire `Build-TessyClone-ISO.ps1`

Avviare lo script PowerShell dalla root del progetto (lo script richiede automaticamente l'elevazione ai privilegi di Amministratore necessari per `DISM`):

```powershell
.\Build-TessyClone-ISO.ps1
```

Lo script esegue automaticamente i seguenti passaggi:
1. Monta `WinPE_Build_Work\ISO_Root\sources\boot.wim` tramite `DISM`.
2. Inietta `TessyClone.exe`, `winpeshl.ini` e gli sfondi presenti in `assets/`.
3. Integra ricorsivamente tutti i driver `.inf` trovati nella cartella `Drivers/`.
4. Salva le modifiche su `boot.wim` (`/Commit`) e configura i file BCD BIOS/UEFI (`quietboot`, `bootux disabled`).
5. Genera la ISO finale **`WinPE_Cloner_x64.iso`** con supporto Dual-Boot El Torito (BIOS + UEFI), pronta per essere scritta su chiavetta USB con Rufus o Ventoy.

#### Parametri opzionali

È possibile personalizzare i percorsi passando parametri allo script:

```powershell
.\Build-TessyClone-ISO.ps1 `
    -IsoRootDir "C:\percorso\ISO_Root" `
    -DriversDir "C:\percorso\Drivers" `
    -OutputIso "C:\percorso\TessyClone.iso" `
    -OscdimgExe "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
```

## Licenza

Questo progetto è distribuito sotto licenza **MIT**. Vedi il file [LICENSE](LICENSE) per i dettagli.
