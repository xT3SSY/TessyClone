using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WinPECloner
{
    public class CloneEngine
    {
        // 512 KB I/O block size (512 * 1024 byte): mantiene alte velocità (> 1000 MB/s)
        // e riduce drasticamente i picchi di carico sui controller NVMe/SATA sensibili (es. Micron 2400 QLC).
        private const int CHUNK_SIZE = 512 * 1024;

        private static unsafe bool CheckIsAllZero(IntPtr pointer, int length)
        {
            var span = new ReadOnlySpan<byte>((void*)pointer, length);
            return span.IndexOfAnyExcept((byte)0) == -1;
        }

        public static async Task<bool> CloneOneToManyAsync(
            DiskInfo sourceDisk,
            List<DiskInfo> targetDisks,
            IProgress<CloneProgressReport>? progress,
            Action<string>? log,
            CancellationToken ct,
            bool parallelMode = false)
        {
            if (targetDisks == null || targetDisks.Count == 0)
            {
                log?.Invoke("ERRORE: Nessun disco di destinazione selezionato.");
                return false;
            }

            int srcDiskNum = sourceDisk.DiskNumber;
            long totalSourceBytes = sourceDisk.TotalSizeBytes;

            if (totalSourceBytes <= 0)
            {
                log?.Invoke($"ERRORE CRITICO: Il disco sorgente riporta capacità non valida ({totalSourceBytes} byte). Clonazione interrotta.");
                return false;
            }

            // 1. Pre-controlli di sicurezza sulle destinazioni
            log?.Invoke("Avvio pre-controlli di sicurezza sulle destinazioni...");
            var validTargets = new List<DiskInfo>();

            foreach (var target in targetDisks)
            {
                if (target.DiskNumber == srcDiskNum)
                {
                    log?.Invoke($"SICUREZZA: Il Disco {target.DiskNumber} è la sorgente. Escluso dalle destinazioni.");
                    continue;
                }

                if (target.TotalSizeBytes <= 0 || target.IsOffline)
                {
                    log?.Invoke($"ATTENZIONE: Il Disco {target.DiskNumber} ha capacità 0 byte o risulta offline. Escluso.");
                    continue;
                }

                validTargets.Add(target);
            }

            if (validTargets.Count == 0)
            {
                log?.Invoke("ERRORE: Nessun disco di destinazione valido o pronto per la scrittura.");
                return false;
            }

            // 2. Controllo Capacità (Sorgente vs Destinazione)
            foreach (var target in validTargets)
            {
                if (sourceDisk.TotalSizeBytes > target.TotalSizeBytes)
                {
                    log?.Invoke($"ERRORE CAPACITÀ: Il Disco Sorgente {srcDiskNum} ({sourceDisk.CleanCapacity}) è più grande del Disco Destinazione {target.DiskNumber} ({target.CleanCapacity}).");
                    return false;
                }
            }

            int maxConcurrency = parallelMode ? 2 : 1;
            log?.Invoke($"Modalità di clonazione: {(parallelMode ? "Parallela (max 2 dischi simultanei)" : "Sequenziale (1 disco alla volta)")} con buffer 512 KB Direct I/O e Zero-Skip.");

            // Inizializza stato per tutti i dischi
            var targetStatuses = new ConcurrentDictionary<int, string>();
            var targetBytesProcessedMap = new ConcurrentDictionary<int, long>();
            var targetSpeedMap = new ConcurrentDictionary<int, double>();

            foreach (var t in validTargets)
            {
                targetStatuses[t.DiskNumber] = "In coda...";
                targetBytesProcessedMap[t.DiskNumber] = 0L;
                targetSpeedMap[t.DiskNumber] = 0.0;
            }

            long totalFailedBytesRemaining = 0L;
            int resetSpeedRequested = 0;

            // Invia report iniziale con tutti i dischi "In coda..."
            progress?.Report(new CloneProgressReport
            {
                BytesCopied = 0L,
                TotalBytes = totalSourceBytes * validTargets.Count,
                SpeedMBs = 0.0,
                Elapsed = TimeSpan.Zero,
                EstimatedRemaining = TimeSpan.Zero,
                TargetStatuses = new Dictionary<int, string>(targetStatuses)
            });

            // Monitor background per aggiornamento barra globale e stima tempo
            using var monitorCts = new CancellationTokenSource();
            var overallSw = Stopwatch.StartNew();
            long initialGrandTotalBytes = totalSourceBytes * validTargets.Count;

            var monitorTask = Task.Run(async () =>
            {
                var speedQueue = new Queue<(DateTime Time, long Bytes)>();
                speedQueue.Enqueue((DateTime.UtcNow, 0L));

                while (!monitorCts.Token.IsCancellationRequested)
                {
                    long sumBytes = targetBytesProcessedMap.Values.Sum();
                    var now = DateTime.UtcNow;

                    if (Interlocked.Exchange(ref resetSpeedRequested, 0) == 1)
                    {
                        speedQueue.Clear();
                        speedQueue.Enqueue((now, sumBytes));
                    }
                    else
                    {
                        speedQueue.Enqueue((now, sumBytes));
                    }

                    while (speedQueue.Count > 2 && (now - speedQueue.Peek().Time).TotalSeconds > 2.0)
                    {
                        speedQueue.Dequeue();
                    }

                    var oldest = speedQueue.Peek();
                    double elapsedSec = (now - oldest.Time).TotalSeconds;
                    double overallSpeed = 0;
                    if (elapsedSec >= 0.25)
                    {
                        overallSpeed = ((sumBytes - oldest.Bytes) / (1024.0 * 1024.0)) / elapsedSec;
                    }

                    // Se la velocità globale calcolata è transitoriamente bassa ma un disco attivo sta scrivendo,
                    // usa la velocità istantanea dei dischi attivi
                    double activeDisksSpeed = targetSpeedMap.Values.Where(s => s > 0).Sum();
                    double displaySpeed = overallSpeed > 0.1 ? overallSpeed : activeDisksSpeed;

                    // Verifica se qualche disco è in fase di finalizzazione (DiskPart)
                    var finalizingEntry = targetStatuses.FirstOrDefault(kv => kv.Value.Contains("Finalizzazione", StringComparison.OrdinalIgnoreCase));
                    string? currentPhaseMsg = !string.IsNullOrEmpty(finalizingEntry.Value) ? finalizingEntry.Value : null;

                    long currentFailedRemaining = Interlocked.Read(ref totalFailedBytesRemaining);
                    long effectiveGrandTotal = Math.Max(sumBytes, initialGrandTotalBytes - currentFailedRemaining);
                    long remainingToCopy = Math.Max(0L, effectiveGrandTotal - sumBytes);

                    double rawPct = effectiveGrandTotal > 0 ? (double)sumBytes / effectiveGrandTotal * 100.0 : 0.0;
                    // Mantieni al 99% durante la copia e fino al termine della finalizzazione di tutti i dischi
                    double displayPct = Math.Min(99.0, rawPct);

                    TimeSpan eta = TimeSpan.Zero;
                    if (remainingToCopy > 0 && displaySpeed > 0.05)
                    {
                        eta = TimeSpan.FromSeconds(remainingToCopy / (displaySpeed * 1024.0 * 1024.0));
                    }

                    progress?.Report(new CloneProgressReport
                    {
                        BytesCopied = sumBytes,
                        TotalBytes = effectiveGrandTotal,
                        Percentage = displayPct,
                        SpeedMBs = displaySpeed,
                        Elapsed = overallSw.Elapsed,
                        EstimatedRemaining = eta,
                        PhaseMessage = currentPhaseMsg,
                        TargetStatuses = new Dictionary<int, string>(targetStatuses)
                    });

                    try
                    {
                        await Task.Delay(250, monitorCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, monitorCts.Token);

            // Esecuzione controllata tramite SemaphoreSlim (1 disco per sequenziale, 2 per parallela)
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var tasks = new List<Task>();
            int successCount = 0;

            foreach (var target in validTargets)
            {
                if (ct.IsCancellationRequested) break;

                await semaphore.WaitAsync(ct);

                var targetDisk = target;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        if (ct.IsCancellationRequested)
                        {
                            targetStatuses[targetDisk.DiskNumber] = "Annullato";
                            long rem = Math.Max(0L, totalSourceBytes - targetBytesProcessedMap.GetValueOrDefault(targetDisk.DiskNumber, 0L));
                            Interlocked.Add(ref totalFailedBytesRemaining, rem);
                            return;
                        }

                        // Segnala azzeramento media mobile all'avvio del disco in coda
                        Interlocked.Exchange(ref resetSpeedRequested, 1);

                        bool ok = await CloneSingleDiskAsync(
                            sourceDisk,
                            targetDisk,
                            targetStatuses,
                            targetBytesProcessedMap,
                            targetSpeedMap,
                            log,
                            ct);

                        if (ok)
                        {
                            Interlocked.Increment(ref successCount);
                        }
                        else
                        {
                            long processed = targetBytesProcessedMap.GetValueOrDefault(targetDisk.DiskNumber, 0L);
                            long uncopied = Math.Max(0L, totalSourceBytes - processed);
                            Interlocked.Add(ref totalFailedBytesRemaining, uncopied);
                            targetSpeedMap[targetDisk.DiskNumber] = 0.0;
                            log?.Invoke($"[AVVISO] Clonazione non riuscita su Disco {targetDisk.DiskNumber}. Dimensione residua ({DiskInfo.FormatCleanSize(uncopied)}) esclusa dal totale.");
                        }
                    }
                    finally
                    {
                        // Chiusura handle già effettuata: pausa di stabilizzazione bus controller PCIe/NVMe (3s) prima del disco successivo
                        log?.Invoke($"Pausa di stabilizzazione bus controller PCIe/NVMe (3s) per Disco {targetDisk.DiskNumber}...");
                        try
                        {
                            await Task.Delay(3000, CancellationToken.None);
                        }
                        catch { }
                        semaphore.Release();
                    }
                }, ct);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            // Arresta monitor
            monitorCts.Cancel();
            try { await monitorTask; } catch { }

            long finalSumBytes = targetBytesProcessedMap.Values.Sum();
            long finalFailedRemaining = Interlocked.Read(ref totalFailedBytesRemaining);
            long finalEffectiveTotal = Math.Max(finalSumBytes, initialGrandTotalBytes - finalFailedRemaining);

            // Invia report finale con stato 100% definitivo a lavori davvero conclusi
            progress?.Report(new CloneProgressReport
            {
                BytesCopied = finalSumBytes,
                TotalBytes = finalEffectiveTotal,
                Percentage = 100.0,
                SpeedMBs = 0.0,
                Elapsed = overallSw.Elapsed,
                EstimatedRemaining = TimeSpan.Zero,
                PhaseMessage = null,
                TargetStatuses = new Dictionary<int, string>(targetStatuses)
            });

            overallSw.Stop();
            bool anySuccess = successCount > 0;
            log?.Invoke(anySuccess
                ? $"[OK] Operazione completata: {successCount}/{validTargets.Count} disco/i clonati con successo in {overallSw.Elapsed:hh\\:mm\\:ss}!"
                : "[ERRORE] Nessun disco clonato con successo. Controllare i log.");

            return anySuccess;
        }

        private static async Task<bool> CloneSingleDiskAsync(
            DiskInfo sourceDisk,
            DiskInfo targetDisk,
            ConcurrentDictionary<int, string> targetStatuses,
            ConcurrentDictionary<int, long> targetBytesProcessedMap,
            ConcurrentDictionary<int, double> targetSpeedMap,
            Action<string>? log,
            CancellationToken ct)
        {
            int srcDiskNum = sourceDisk.DiskNumber;
            int dstNum = targetDisk.DiskNumber;
            long totalSourceBytes = sourceDisk.TotalSizeBytes;
            string dstPath = $@"\\.\PhysicalDrive{dstNum}";
            string srcPath = $@"\\.\PhysicalDrive{srcDiskNum}";

            log?.Invoke($"[AVVIO] Preparazione Disco {dstNum} ({targetDisk.Model} — {targetDisk.CleanCapacity})...");
            targetStatuses[dstNum] = "In preparazione...";

            // Sblocco e cancellazione layout precedente
            DiskManager.PrepareTargetDiskForRawWriting(dstNum, log);

            // Apertura sorgente in lettura Direct I/O (senza WRITE_THROUGH)
            SafeFileHandle? hSrc = NativeMethods.CreateFileW(
                srcPath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);

            if (hSrc.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                log?.Invoke($"[ERRORE CRITICO SORGENTE] Impossibile aprire {srcPath}. Win32: {err}");
                targetStatuses[dstNum] = $"FALLITO (Win32 {err})";
                return false;
            }

            // Apertura destinazione in scrittura Direct I/O (SOLO NO_BUFFERING, senza WRITE_THROUGH)
            SafeFileHandle? hDst = NativeMethods.CreateFileW(
                dstPath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            if (hDst.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                log?.Invoke($"[ERRORE DESTINAZIONE] Impossibile aprire {dstPath}. Codice Win32: {err} (0x{err:X8}).");
                targetStatuses[dstNum] = $"FALLITO (Win32 {err})";
                try { hSrc.Dispose(); } catch { }
                return false;
            }

            // Allocazione buffer allineato a 4096 byte da 512 KB
            IntPtr pBuffer = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)CHUNK_SIZE,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pBuffer == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                log?.Invoke($"[ERRORE ALLOCAZIONE] Impossibile allocare buffer 512 KB per Disco {dstNum}. Win32: {err}");
                targetStatuses[dstNum] = $"FALLITO (Win32 {err})";
                try { hDst.Dispose(); } catch { }
                try { hSrc.Dispose(); } catch { }
                return false;
            }

            long bytesProcessed = 0L;
            long totalBytesWritten = 0L;
            long totalBytesSkipped = 0L;
            var swDisk = Stopwatch.StartNew();

            var speedQueue = new Queue<(DateTime Time, long Bytes)>();
            speedQueue.Enqueue((DateTime.UtcNow, 0L));
            double currentSpeedMBs = 0;
            var reportSw = Stopwatch.StartNew();

            bool cloneOk = true;

            // Esegui TRIM completo prima della copia raw per abilitare Zero-Skip in sicurezza
            long dstDiskSize = targetDisk.TotalSizeBytes > 0 ? targetDisk.TotalSizeBytes : DiskManager.GetDiskLength(hDst);
            bool zeroSkipEnabled = DiskManager.TryTrimEntireDisk(hDst, dstNum, dstDiskSize, log);

            try
            {
                log?.Invoke($"[CLONAZIONE] Copia Disco {srcDiskNum} -> Disco {dstNum} in corso...");
                targetStatuses[dstNum] = "Clonazione 0% (0 MB/s)";

                await Task.Run(() =>
                {
                    while (bytesProcessed < totalSourceBytes)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            targetStatuses[dstNum] = "Annullato";
                            cloneOk = false;
                            break;
                        }

                        long remaining = totalSourceBytes - bytesProcessed;
                        long toRead = Math.Min((long)CHUNK_SIZE, remaining);
                        uint alignedToRead = (uint)((toRead + 4095u) & ~4095u);

                        if (alignedToRead > (uint)toRead)
                        {
                            NativeMethods.ZeroMemory(pBuffer, (IntPtr)alignedToRead);
                        }

                        // Lettura da Sorgente
                        NativeMethods.SetFilePointerEx(hSrc, bytesProcessed, out _, 0 /* FILE_BEGIN */);
                        bool readOk = NativeMethods.ReadFile(hSrc, pBuffer, alignedToRead, out uint bytesRead, IntPtr.Zero);
                        if (!readOk || bytesRead == 0)
                        {
                            // Tentativo fallback su allineamento 512 byte
                            uint aligned512 = (uint)((toRead + 511u) & ~511u);
                            if (aligned512 != alignedToRead)
                            {
                                readOk = NativeMethods.ReadFile(hSrc, pBuffer, aligned512, out bytesRead, IntPtr.Zero);
                            }

                            if (!readOk || bytesRead == 0)
                            {
                                int err = Marshal.GetLastWin32Error();
                                log?.Invoke($"[ERRORE LETTURA SORGENTE] Offset {bytesProcessed:N0}. Win32: {err} (0x{err:X8}).");
                                targetStatuses[dstNum] = $"FALLITO (Win32 {err})";
                                cloneOk = false;
                                break;
                            }
                        }

                        long validBytes = Math.Min((long)bytesRead, remaining);
                        uint alignedToWrite = (uint)((validBytes + 4095u) & ~4095u);

                        // Controllo SIMD zeri sul blocco da 512 KB (solo se Zero-Skip abilitato tramite TRIM)
                        bool isAllZero = zeroSkipEnabled && CheckIsAllZero(pBuffer, (int)alignedToWrite);

                        if (isAllZero)
                        {
                            // Zero-Skip: avanza puntatore senza scrittura fisica
                            totalBytesSkipped += validBytes;
                            if (!NativeMethods.SetFilePointerEx(hDst, bytesProcessed + alignedToWrite, out _, 0 /* FILE_BEGIN */))
                            {
                                int err = Marshal.GetLastWin32Error();
                                log?.Invoke($"[ERRORE SET FILE POINTER] Disco {dstNum} all'offset {bytesProcessed:N0}. Win32: {err}.");
                                targetStatuses[dstNum] = $"FALLITO (Win32 {err})";
                                cloneOk = false;
                                try { hDst?.Dispose(); } catch { }
                                try { hSrc?.Dispose(); } catch { }
                                break;
                            }
                        }
                        else
                        {
                            // Scrittura fisica blocco da 512 KB
                            totalBytesWritten += validBytes;
                            NativeMethods.SetFilePointerEx(hDst, bytesProcessed, out _, 0 /* FILE_BEGIN */);
                            bool writeOk = NativeMethods.WriteFile(hDst, pBuffer, alignedToWrite, out uint written, IntPtr.Zero);
                            if (!writeOk || written < alignedToWrite)
                            {
                                int err = Marshal.GetLastWin32Error();
                                log?.Invoke($"[ERRORE I/O SCRITTURA] Disco {dstNum} all'offset {bytesProcessed:N0}. Codice Win32: {err} (0x{err:X8}).");
                                targetStatuses[dstNum] = $"FALLITO (Win32 {err})";
                                cloneOk = false;
                                try { hDst?.Dispose(); } catch { }
                                try { hSrc?.Dispose(); } catch { }
                                break;
                            }
                        }

                        bytesProcessed += validBytes;
                        targetBytesProcessedMap[dstNum] = bytesProcessed;

                        // Aggiornamento velocità ogni 250 ms
                        if (reportSw.ElapsedMilliseconds >= 250 || bytesProcessed >= totalSourceBytes)
                        {
                            reportSw.Restart();
                            var now = DateTime.UtcNow;
                            speedQueue.Enqueue((now, bytesProcessed));
                            while (speedQueue.Count > 2 && (now - speedQueue.Peek().Time).TotalSeconds > 2.0)
                            {
                                speedQueue.Dequeue();
                            }

                            var oldest = speedQueue.Peek();
                            double elapsedSec = (now - oldest.Time).TotalSeconds;
                            if (elapsedSec >= 0.25)
                            {
                                long delta = bytesProcessed - oldest.Bytes;
                                currentSpeedMBs = (delta / (1024.0 * 1024.0)) / elapsedSec;
                            }

                            targetSpeedMap[dstNum] = currentSpeedMBs;
                            double pct = (double)bytesProcessed / totalSourceBytes * 100.0;
                            targetStatuses[dstNum] = $"Clonazione {pct:F0}% ({currentSpeedMBs:F0} MB/s)";
                        }
                    }
                }, ct);

                if (cloneOk && bytesProcessed >= totalSourceBytes)
                {
                    // Flush buffer eseguito UNA SOLA VOLTA alla fine della clonazione del disco
                    NativeMethods.FlushFileBuffers(hDst);
                    NativeMethods.DeviceIoControl(
                        hDst,
                        NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
                        IntPtr.Zero, 0,
                        IntPtr.Zero, 0,
                        out _,
                        IntPtr.Zero);

                    // Chiudi immediatamente gli handle del disco per permettere l'espansione e rilasciare il controller
                    try { hDst?.Dispose(); } catch { }
                    hDst = null;
                    try { hSrc?.Dispose(); } catch { }
                    hSrc = null;

                    double writtenGB = totalBytesWritten / (1024.0 * 1024.0 * 1024.0);
                    double skippedGB = totalBytesSkipped / (1024.0 * 1024.0 * 1024.0);
                    log?.Invoke($"[OK] Copia raw completata su Disco {dstNum}: {writtenGB:F1} GB scritti, {skippedGB:F1} GB vuoti saltati in {swDisk.Elapsed:mm\\:ss}!");

                    // Notifica fase di finalizzazione (DiskPart)
                    string finalMsg = $"Finalizzazione partizioni su Disco {dstNum} in corso (attendere)...";
                    targetStatuses[dstNum] = finalMsg;
                    targetSpeedMap[dstNum] = 0.0;
                    log?.Invoke(finalMsg);

                    bool isGpt = DiskManager.IsDiskGpt(dstNum) ??
                                 (sourceDisk.PartitionStyle == NativeMethods.PARTITION_STYLE.PARTITION_STYLE_GPT);
                    bool needExtend = targetDisk.TotalSizeBytes >= totalSourceBytes + (1024L * 1024L * 1024L);

                    if (needExtend)
                    {
                        log?.Invoke($"Rilevata capienza superiore su Disco {dstNum} ({targetDisk.CleanCapacity} > {sourceDisk.CleanCapacity}). Avvio finalizzazione completa (Step A/B: espansione + BCD)...");
                    }
                    else
                    {
                        log?.Invoke($"Avvio finalizzazione su Disco {dstNum} (Step A/B: firma disco + rigenerazione BCD)...");
                    }

                    targetStatuses[dstNum] = $"Finalizzazione disco e bootloader (BCD) su Disco {dstNum} in corso...";
                    try
                    {
                        DiskManager.FinalizeClonedDisk(dstNum, needExtend, targetDisk.TotalSizeBytes, isGpt, log);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[ERRORE] Eccezione durante la finalizzazione su Disco {dstNum}: {ex.Message}");
                    }

                    targetStatuses[dstNum] = "Completato (OK)";
                    return true;
                }
                else
                {
                    if (ct.IsCancellationRequested)
                    {
                        targetStatuses[dstNum] = "Annullato";
                    }
                    else if (!targetStatuses[dstNum].StartsWith("FALLITO", StringComparison.OrdinalIgnoreCase))
                    {
                        targetStatuses[dstNum] = "FALLITO";
                    }
                    return false;
                }
            }
            finally
            {
                try { hDst?.Dispose(); } catch { }
                try { hSrc?.Dispose(); } catch { }
                if (pBuffer != IntPtr.Zero)
                {
                    NativeMethods.VirtualFree(pBuffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }
            }
        }
    }
}
