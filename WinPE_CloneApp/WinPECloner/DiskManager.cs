using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WinPECloner
{
    public class DiskManager
    {
        private static readonly object _diskpartLock = new object();

        public static void RescanBus(Action<string>? log = null)
        {
            // 1. Notifica IOCTL_DISK_UPDATE_PROPERTIES su tutti i PhysicalDrive aperti
            for (int i = 0; i < 32; i++)
            {
                try
                {
                    using SafeFileHandle h = NativeMethods.CreateFileW(
                        $@"\\.\PhysicalDrive{i}",
                        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        NativeMethods.OPEN_EXISTING,
                        0,
                        IntPtr.Zero);

                    if (!h.IsInvalid)
                    {
                        NativeMethods.DeviceIoControl(
                            h,
                            NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
                            IntPtr.Zero, 0,
                            IntPtr.Zero, 0,
                            out _,
                            IntPtr.Zero);
                    }
                }
                catch { }
            }

            // 2. Rescan rapido diskpart per riallineare MountMgr/PartMgr di Windows/WinPE
            try
            {
                string scriptFile = Path.Combine(Path.GetTempPath(), "bus_rescan.txt");
                File.WriteAllText(scriptFile, "rescan\nexit\n");
                var psi = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = $"/s \"{scriptFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(4000);
                }
                if (File.Exists(scriptFile))
                {
                    try { File.Delete(scriptFile); } catch { }
                }
            }
            catch { }
        }

        public static List<DiskInfo> EnumerateDisks(int maxDisks = 32)
        {
            var diskList = new List<DiskInfo>();
            var volumeToDiskMap = BuildVolumeToDiskMapping(out var volumeUsedSpaceMap);
            var wmiSizes = GetWmiDiskSizes();

            for (int diskNum = 0; diskNum < maxDisks; diskNum++)
            {
                string devicePath = $@"\\.\PhysicalDrive{diskNum}";
                using SafeFileHandle hDisk = NativeMethods.CreateFileW(
                    devicePath,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (hDisk.IsInvalid)
                {
                    continue;
                }

                var diskInfo = new DiskInfo
                {
                    DiskNumber = diskNum
                };

                // 1. Dimensione reale del disco (IOCTL_DISK_GET_LENGTH_INFO 0x0007405C con fallback WMI e Geometria)
                long diskLength = GetDiskLength(hDisk);
                if (diskLength <= 0 && wmiSizes.TryGetValue(diskNum, out long wmiSize))
                {
                    diskLength = wmiSize;
                }

                // Nascondi chiavette USB / pendrive inferiori a 100 GB (es. chiavetta WinPE da 7 GB)
                // MA NON nascondere unità con capacità non determinata o momentaneamente a 0 byte (NVMe offline/rescan)
                if (diskLength > 0 && diskLength < 100L * 1024 * 1024 * 1024)
                {
                    continue;
                }

                diskInfo.TotalSizeBytes = diskLength;
                diskInfo.SectorSize = GetSectorSize(hDisk);
                diskInfo.IsOffline = (diskInfo.TotalSizeBytes <= 0);

                // 2. Proprietà Hardware (Modello, Seriale, Bus Type)
                QueryDeviceProperties(hDisk, diskInfo);

                // 3. Stile partizione (MBR / GPT / RAW)
                diskInfo.PartitionStyle = QueryPartitionStyle(hDisk);

                // 4. Volumi associati e calcolo spazio occupato
                if (volumeToDiskMap.TryGetValue(diskNum, out var volumes))
                {
                    diskInfo.Volumes = volumes;
                }

                if (volumeUsedSpaceMap.TryGetValue(diskNum, out long usedSpace))
                {
                    diskInfo.UsedSizeBytes = usedSpace;
                }

                // 5. Ispezione fisica della tabella partizioni (per dischi offline o senza lettere di unità montate)
                QueryPhysicalPartitions(hDisk, diskInfo);

                diskList.Add(diskInfo);
            }

            return diskList;
        }

        public static long GetDiskLength(SafeFileHandle hDisk)
        {
            // Metodo 1 primario: IOCTL_DISK_GET_LENGTH_INFO (0x0007405C) - Esattamente 8 byte Int64
            int size = sizeof(long);
            IntPtr pOut = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                    IntPtr.Zero, 0,
                    pOut, (uint)size,
                    out uint returned,
                    IntPtr.Zero))
                {
                    long length = Marshal.ReadInt64(pOut);
                    if (length > 0 && length < 100_000_000_000_000_000L) // sanity check (< 100 Petabyte)
                    {
                        return length;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pOut);
            }

            // Metodo 2 fallback: IOCTL_DISK_GET_DRIVE_GEOMETRY_EX
            return GetDiskLengthFromGeometry(hDisk);
        }

        private static long GetDiskLengthFromGeometry(SafeFileHandle hDisk)
        {
            int outSize = Marshal.SizeOf<NativeMethods.DISK_GEOMETRY_EX>() + 512;
            IntPtr pOut = Marshal.AllocHGlobal(outSize);
            try
            {
                if (NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                    IntPtr.Zero, 0,
                    pOut, (uint)outSize,
                    out _,
                    IntPtr.Zero))
                {
                    var geomEx = Marshal.PtrToStructure<NativeMethods.DISK_GEOMETRY_EX>(pOut);
                    if (geomEx.DiskSize > 0)
                    {
                        return geomEx.DiskSize;
                    }
                    if (geomEx.Geometry.Cylinders > 0 && geomEx.Geometry.TracksPerCylinder > 0 &&
                        geomEx.Geometry.SectorsPerTrack > 0 && geomEx.Geometry.BytesPerSector > 0)
                    {
                        return geomEx.Geometry.Cylinders *
                               geomEx.Geometry.TracksPerCylinder *
                               geomEx.Geometry.SectorsPerTrack *
                               geomEx.Geometry.BytesPerSector;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pOut);
            }
            return 0;
        }

        private static Dictionary<int, long> GetWmiDiskSizes()
        {
            var map = new Dictionary<int, long>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Index, Size FROM Win32_DiskDrive");
                foreach (ManagementObject mo in searcher.Get())
                {
                    if (mo["Index"] != null && mo["Size"] != null)
                    {
                        int index = Convert.ToInt32(mo["Index"]);
                        long size = Convert.ToInt64(mo["Size"]);
                        map[index] = size;
                    }
                }
            }
            catch
            {
                // In ambiente WinPE senza supporto WMI opzionale non sollevare eccezioni
            }
            return map;
        }

        private static NativeMethods.PARTITION_STYLE QueryPartitionStyle(SafeFileHandle hDisk)
        {
            int outSize = 1024 * 32; // Buffer ampio per DRIVE_LAYOUT_INFORMATION_EX
            IntPtr pOut = Marshal.AllocHGlobal(outSize);
            try
            {
                if (NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                    IntPtr.Zero, 0,
                    pOut, (uint)outSize,
                    out _,
                    IntPtr.Zero))
                {
                    var layout = Marshal.PtrToStructure<NativeMethods.DRIVE_LAYOUT_INFORMATION_EX>(pOut);
                    return layout.PartitionStyle;
                }
            }
            catch
            {
                return NativeMethods.PARTITION_STYLE.PARTITION_STYLE_RAW;
            }
            finally
            {
                Marshal.FreeHGlobal(pOut);
            }
            return NativeMethods.PARTITION_STYLE.PARTITION_STYLE_RAW;
        }

        private static uint GetSectorSize(SafeFileHandle hDisk)
        {
            int outSize = Marshal.SizeOf<NativeMethods.DISK_GEOMETRY_EX>() + 256;
            IntPtr pOut = Marshal.AllocHGlobal(outSize);
            try
            {
                if (NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                    IntPtr.Zero, 0,
                    pOut, (uint)outSize,
                    out _,
                    IntPtr.Zero))
                {
                    var geom = Marshal.PtrToStructure<NativeMethods.DISK_GEOMETRY_EX>(pOut);
                    return geom.Geometry.BytesPerSector > 0 ? geom.Geometry.BytesPerSector : 512;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pOut);
            }
            return 512;
        }

        private static void QueryPhysicalPartitions(SafeFileHandle hDisk, DiskInfo diskInfo)
        {
            if (diskInfo.TotalSizeBytes <= 0) return;

            byte[] sector0 = new byte[512];
            byte[] sector1 = new byte[512];

            NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
            if (!NativeMethods.ReadFile(hDisk, sector0, 512, out uint r0, IntPtr.Zero) || r0 < 512) return;

            NativeMethods.SetFilePointerEx(hDisk, 512, out _, 0);
            if (!NativeMethods.ReadFile(hDisk, sector1, 512, out uint r1, IntPtr.Zero) || r1 < 512) return;

            ulong gptSig = BitConverter.ToUInt64(sector1, 0);
            bool isGpt = (gptSig == 0x5452415020494645UL); // "EFI PART"

            if (isGpt)
            {
                diskInfo.PartitionStyle = NativeMethods.PARTITION_STYLE.PARTITION_STYLE_GPT;

                long partEntryLba = BitConverter.ToInt64(sector1, 0x48);
                int numEntries = BitConverter.ToInt32(sector1, 0x50);
                int sizeEntry = BitConverter.ToInt32(sector1, 0x54);

                if (numEntries > 0 && sizeEntry >= 128)
                {
                    int tableBytes = Math.Min(numEntries * sizeEntry, 16384);
                    byte[] tableBuffer = new byte[tableBytes];

                    NativeMethods.SetFilePointerEx(hDisk, partEntryLba * 512, out _, 0);
                    if (NativeMethods.ReadFile(hDisk, tableBuffer, (uint)tableBytes, out uint rTable, IntPtr.Zero) && rTable == tableBytes)
                    {
                        int activeCount = 0;
                        long largestSize = 0L;
                        long totalAllocated = 0L;
                        Guid basicDataGuid = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
                        long windowsPartSize = 0L;

                        for (int i = 0; i < numEntries; i++)
                        {
                            int off = i * sizeEntry;
                            if (off + sizeEntry > tableBytes) break;

                            byte[] typeGuidBytes = new byte[16];
                            Array.Copy(tableBuffer, off, typeGuidBytes, 0, 16);
                            Guid typeGuid = new Guid(typeGuidBytes);
                            if (typeGuid == Guid.Empty) continue;

                            long startLba = BitConverter.ToInt64(tableBuffer, off + 0x20);
                            long endLba = BitConverter.ToInt64(tableBuffer, off + 0x28);
                            if (startLba > 0 && endLba >= startLba)
                            {
                                activeCount++;
                                long sizeBytes = (endLba - startLba + 1) * 512;
                                totalAllocated += sizeBytes;

                                if (typeGuid == basicDataGuid && sizeBytes > windowsPartSize)
                                {
                                    windowsPartSize = sizeBytes;
                                }
                                if (sizeBytes > largestSize)
                                {
                                    largestSize = sizeBytes;
                                }
                            }
                        }

                        if (activeCount > 0)
                        {
                            diskInfo.PhysicalPartitionsAllocatedBytes = totalAllocated;
                            string winSizeFormatted = DiskInfo.FormatCleanSize(windowsPartSize > 0 ? windowsPartSize : largestSize);
                            diskInfo.PhysicalPartitionSummary = $"GPT: {activeCount} partizioni (Windows {winSizeFormatted})";

                            if (diskInfo.IsOffline)
                            {
                                diskInfo.PhysicalPartitionSummary += " — Offline (Conflitto GUID)";
                            }
                        }
                    }
                }
            }
            else
            {
                int mbrCount = 0;
                long largestMbr = 0L;
                long totalMbrAllocated = 0L;

                for (int i = 0; i < 4; i++)
                {
                    int off = 0x1BE + (i * 16);
                    byte partType = sector0[off + 4];
                    uint sectorCount = BitConverter.ToUInt32(sector0, off + 12);
                    if (partType != 0 && sectorCount > 0)
                    {
                        mbrCount++;
                        long size = (long)sectorCount * 512;
                        totalMbrAllocated += size;
                        if (size > largestMbr) largestMbr = size;
                    }
                }

                if (mbrCount > 0)
                {
                    diskInfo.PartitionStyle = NativeMethods.PARTITION_STYLE.PARTITION_STYLE_MBR;
                    diskInfo.PhysicalPartitionsAllocatedBytes = totalMbrAllocated;
                    string formatted = DiskInfo.FormatCleanSize(largestMbr);
                    diskInfo.PhysicalPartitionSummary = $"MBR: {mbrCount} partizioni (Windows {formatted})";

                    if (diskInfo.IsOffline)
                    {
                        diskInfo.PhysicalPartitionSummary += " — Offline (Conflitto Firma)";
                    }
                }
            }
        }

        private static void QueryDeviceProperties(SafeFileHandle hDisk, DiskInfo diskInfo)
        {
            var query = new NativeMethods.STORAGE_PROPERTY_QUERY
            {
                PropertyId = 0,
                QueryType = 0
            };

            int inSize = Marshal.SizeOf(query);
            IntPtr pIn = Marshal.AllocHGlobal(inSize);
            Marshal.StructureToPtr(query, pIn, false);

            int outBufferSize = 4096;
            IntPtr pOut = Marshal.AllocHGlobal(outBufferSize);

            try
            {
                if (NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                    pIn, (uint)inSize,
                    pOut, (uint)outBufferSize,
                    out uint returned,
                    IntPtr.Zero))
                {
                    var descriptor = Marshal.PtrToStructure<NativeMethods.STORAGE_DEVICE_DESCRIPTOR>(pOut);

                    diskInfo.BusType = descriptor.BusType switch
                    {
                        NativeMethods.STORAGE_BUS_TYPE.BusTypeNvme => "NVMe",
                        NativeMethods.STORAGE_BUS_TYPE.BusTypeSata => "SATA",
                        NativeMethods.STORAGE_BUS_TYPE.BusTypeUsb => "USB",
                        NativeMethods.STORAGE_BUS_TYPE.BusTypeScsi => "SCSI",
                        NativeMethods.STORAGE_BUS_TYPE.BusTypeRAID => "RAID",
                        NativeMethods.STORAGE_BUS_TYPE.BusTypeAta => "ATA/IDE",
                        _ => descriptor.BusType.ToString().Replace("BusType", "")
                    };

                    string vendor = "";
                    string product = "";
                    string serial = "";

                    if (descriptor.VendorIdOffset > 0 && descriptor.VendorIdOffset < returned)
                    {
                        vendor = Marshal.PtrToStringAnsi(IntPtr.Add(pOut, (int)descriptor.VendorIdOffset))?.Trim() ?? "";
                    }

                    if (descriptor.ProductIdOffset > 0 && descriptor.ProductIdOffset < returned)
                    {
                        product = Marshal.PtrToStringAnsi(IntPtr.Add(pOut, (int)descriptor.ProductIdOffset))?.Trim() ?? "";
                    }

                    if (descriptor.SerialNumberOffset > 0 && descriptor.SerialNumberOffset < returned)
                    {
                        serial = Marshal.PtrToStringAnsi(IntPtr.Add(pOut, (int)descriptor.SerialNumberOffset))?.Trim() ?? "";
                    }

                    string fullModel = $"{vendor} {product}".Trim();
                    if (!string.IsNullOrEmpty(fullModel))
                    {
                        diskInfo.Model = fullModel;
                    }
                    diskInfo.SerialNumber = serial;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pIn);
                Marshal.FreeHGlobal(pOut);
            }
        }

        private static Dictionary<int, List<string>> BuildVolumeToDiskMapping(out Dictionary<int, long> volumeUsedSpaceMap)
        {
            var map = new Dictionary<int, List<string>>();
            volumeUsedSpaceMap = new Dictionary<int, long>();
            var processedDriveLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Enumera tutti i volumi tramite Volume GUID (\\?\Volume{...}\) inclusi quelli senza lettera montata
            var volNameSb = new System.Text.StringBuilder(1024);
            IntPtr hFind = NativeMethods.FindFirstVolumeW(volNameSb, (uint)volNameSb.Capacity);
            if (hFind != IntPtr.Zero && hFind != new IntPtr(-1))
            {
                try
                {
                    do
                    {
                        string volGuidRoot = volNameSb.ToString();
                        volNameSb.Clear();
                        if (string.IsNullOrWhiteSpace(volGuidRoot)) continue;

                        string rootWithSlash = volGuidRoot.EndsWith("\\") ? volGuidRoot : volGuidRoot + "\\";
                        string deviceNoSlash = volGuidRoot.TrimEnd('\\');

                        using SafeFileHandle hVol = NativeMethods.CreateFileW(
                            deviceNoSlash,
                            0,
                            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                            IntPtr.Zero,
                            NativeMethods.OPEN_EXISTING,
                            0,
                            IntPtr.Zero);

                        if (hVol.IsInvalid) continue;

                        int outSize = Marshal.SizeOf<NativeMethods.VOLUME_DISK_EXTENTS>() + 512;
                        IntPtr pOut = Marshal.AllocHGlobal(outSize);
                        try
                        {
                            if (!NativeMethods.DeviceIoControl(
                                hVol,
                                NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                                IntPtr.Zero, 0,
                                pOut, (uint)outSize,
                                out _,
                                IntPtr.Zero))
                            {
                                continue;
                            }

                            var extents = Marshal.PtrToStructure<NativeMethods.VOLUME_DISK_EXTENTS>(pOut);
                            if (extents.NumberOfDiskExtents == 0) continue;
                            int diskNum = (int)extents.Extent.DiskNumber;

                            // Rileva eventuale lettera di unità montata
                            string mountLetter = string.Empty;
                            char[] pathChars = new char[1024];
                            if (NativeMethods.GetVolumePathNamesForVolumeNameW(rootWithSlash, pathChars, (uint)pathChars.Length, out uint retLen) && retLen > 1)
                            {
                                string rawPaths = new string(pathChars, 0, (int)Math.Min(retLen, (uint)pathChars.Length));
                                string? firstPath = rawPaths.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault(p => p.Length >= 2 && p[1] == ':');
                                if (!string.IsNullOrEmpty(firstPath))
                                {
                                    mountLetter = firstPath.TrimEnd('\\');
                                    processedDriveLetters.Add(mountLetter);
                                }
                            }

                            // Leggi filesystem e spazio reale tramite GetDiskFreeSpaceExW sul percorso Volume GUID (\\?\Volume{...}\)
                            string fsName = "";
                            var fsSb = new System.Text.StringBuilder(64);
                            if (NativeMethods.GetVolumeInformationW(rootWithSlash, null, 0, out _, out _, out _, fsSb, (uint)fsSb.Capacity))
                            {
                                fsName = fsSb.ToString();
                            }

                            long usedOnVol = 0;
                            long totalOnVol = 0;
                            if (NativeMethods.GetDiskFreeSpaceExW(rootWithSlash, out _, out ulong totalBytes, out ulong freeBytes) &&
                                totalBytes > 0 && totalBytes >= freeBytes)
                            {
                                totalOnVol = (long)totalBytes;
                                usedOnVol = (long)(totalBytes - freeBytes);
                            }

                            if (!volumeUsedSpaceMap.ContainsKey(diskNum))
                            {
                                volumeUsedSpaceMap[diskNum] = 0;
                            }
                            volumeUsedSpaceMap[diskNum] += usedOnVol;

                            if (!string.IsNullOrEmpty(mountLetter))
                            {
                                if (!map.ContainsKey(diskNum))
                                {
                                    map[diskNum] = new List<string>();
                                }
                                string volSummary = totalOnVol > 0
                                    ? $"{mountLetter} [{(string.IsNullOrEmpty(fsName) ? "FS" : fsName)}, {DiskInfo.FormatCleanSize(usedOnVol)}/{DiskInfo.FormatCleanSize(totalOnVol)}]"
                                    : mountLetter;
                                map[diskNum].Add(volSummary);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pOut);
                        }
                    }
                    while (NativeMethods.FindNextVolumeW(hFind, volNameSb, (uint)volNameSb.Capacity));
                }
                finally
                {
                    NativeMethods.FindVolumeClose(hFind);
                }
            }

            // 2. Fallback per eventuali lettere di unità non coperte da FindFirstVolumeW
            foreach (var drive in DriveInfo.GetDrives())
            {
                string letter = drive.Name.TrimEnd('\\');
                if (processedDriveLetters.Contains(letter)) continue;

                string volumePath = $@"\\.\{letter}";
                using SafeFileHandle hVol = NativeMethods.CreateFileW(
                    volumePath,
                    0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (hVol.IsInvalid) continue;

                int outSize = Marshal.SizeOf<NativeMethods.VOLUME_DISK_EXTENTS>() + 512;
                IntPtr pOut = Marshal.AllocHGlobal(outSize);
                try
                {
                    if (NativeMethods.DeviceIoControl(
                        hVol,
                        NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        IntPtr.Zero, 0,
                        pOut, (uint)outSize,
                        out _,
                        IntPtr.Zero))
                    {
                        var extents = Marshal.PtrToStructure<NativeMethods.VOLUME_DISK_EXTENTS>(pOut);
                        int diskNum = (int)extents.Extent.DiskNumber;

                        string volSummary = drive.Name;
                        long usedOnVol = 0;
                        try
                        {
                            if (drive.IsReady)
                            {
                                usedOnVol = drive.TotalSize - drive.TotalFreeSpace;
                                string usedFormatted = DiskInfo.FormatCleanSize(usedOnVol);
                                string totalFormatted = DiskInfo.FormatCleanSize(drive.TotalSize);
                                volSummary = $"{letter} [{drive.DriveFormat}, {usedFormatted}/{totalFormatted}]";
                            }
                        }
                        catch { }

                        if (!map.ContainsKey(diskNum))
                        {
                            map[diskNum] = new List<string>();
                        }
                        map[diskNum].Add(volSummary);

                        if (!volumeUsedSpaceMap.ContainsKey(diskNum))
                        {
                            volumeUsedSpaceMap[diskNum] = 0;
                        }
                        volumeUsedSpaceMap[diskNum] += usedOnVol;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pOut);
                }
            }

            return map;
        }

        public static bool LockAndDismountAllVolumesOnDisk(int diskNumber, Action<string>? log = null)
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                string volumePath = $@"\\.\{drive.Name.TrimEnd('\\')}";
                using SafeFileHandle hVol = NativeMethods.CreateFileW(
                    volumePath,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (hVol.IsInvalid) continue;

                int outSize = Marshal.SizeOf<NativeMethods.VOLUME_DISK_EXTENTS>() + 512;
                IntPtr pOut = Marshal.AllocHGlobal(outSize);
                try
                {
                    if (NativeMethods.DeviceIoControl(
                        hVol,
                        NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                        IntPtr.Zero, 0,
                        pOut, (uint)outSize,
                        out _,
                        IntPtr.Zero))
                    {
                        var extents = Marshal.PtrToStructure<NativeMethods.VOLUME_DISK_EXTENTS>(pOut);
                        if (extents.Extent.DiskNumber == diskNumber)
                        {
                            log?.Invoke($"Smontaggio e blocco del volume {drive.Name} su Disco {diskNumber}...");

                            // FSCTL_ALLOW_EXTENDED_DASD_IO su handle di volume prima di lock e dismount
                            NativeMethods.DeviceIoControl(hVol, NativeMethods.FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                            NativeMethods.DeviceIoControl(hVol, NativeMethods.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                            NativeMethods.DeviceIoControl(hVol, NativeMethods.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pOut);
                }
            }
            return true;
        }

        public static bool WipeDisk(int diskNumber, Action<string>? log = null)
        {
            log?.Invoke($"Inizio pulizia disco (Wipe MBR/GPT) su Disco {diskNumber}...");

            LockAndDismountAllVolumesOnDisk(diskNumber, log);

            string devicePath = $@"\\.\PhysicalDrive{diskNumber}";
            using SafeFileHandle hDisk = NativeMethods.CreateFileW(
                devicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_WRITE_THROUGH | NativeMethods.FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            if (hDisk.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                log?.Invoke($"[ERRORE] Impossibile aprire il Disco {diskNumber} in scrittura esclusiva. Win32: {err}");
                return false;
            }

            // Elimina layout partizioni nel kernel
            NativeMethods.DeviceIoControl(
                hDisk,
                NativeMethods.IOCTL_DISK_DELETE_DRIVE_LAYOUT,
                IntPtr.Zero, 0,
                IntPtr.Zero, 0,
                out _,
                IntPtr.Zero);

            long diskSize = GetDiskLength(hDisk);
            if (diskSize <= 0)
            {
                log?.Invoke($"[ERRORE] Il Disco {diskNumber} riporta capacità 0 byte o risulta non pronto.");
                return false;
            }

            int wipeBlockSize = 2 * 1024 * 1024; // 2 MB
            IntPtr pZeroBuffer = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)wipeBlockSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pZeroBuffer == IntPtr.Zero)
            {
                log?.Invoke($"[ERRORE] Allocazione buffer allineato fallita su Disco {diskNumber}.");
                return false;
            }

            NativeMethods.ZeroMemory(pZeroBuffer, (IntPtr)wipeBlockSize);

            try
            {
                // Azzeramento primi 2 MB (MBR/GPT primario)
                NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                NativeMethods.WriteFile(hDisk, pZeroBuffer, (uint)wipeBlockSize, out _, IntPtr.Zero);

                // Azzeramento ultimi 2 MB (Backup GPT secondario)
                if (diskSize > wipeBlockSize)
                {
                    long tailOffset = (diskSize - wipeBlockSize) & ~4095L;
                    NativeMethods.SetFilePointerEx(hDisk, tailOffset, out _, 0);
                    NativeMethods.WriteFile(hDisk, pZeroBuffer, (uint)wipeBlockSize, out _, IntPtr.Zero);
                }

                NativeMethods.FlushFileBuffers(hDisk);

                // Notifica kernel
                NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
                    IntPtr.Zero, 0,
                    IntPtr.Zero, 0,
                    out _,
                    IntPtr.Zero);

                log?.Invoke($"[OK] Pulizia completata su Disco {diskNumber}");
                return true;
            }
            finally
            {
                if (pZeroBuffer != IntPtr.Zero)
                {
                    NativeMethods.VirtualFree(pZeroBuffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }
            }
        }

        public static bool PrepareTargetDiskForRawWriting(int diskNumber, Action<string>? log = null)
        {
            log?.Invoke($"Preparazione e sblocco disco per scrittura raw su Disco {diskNumber}...");
            LockAndDismountAllVolumesOnDisk(diskNumber, log);

            string devicePath = $@"\\.\PhysicalDrive{diskNumber}";
            using SafeFileHandle hDisk = NativeMethods.CreateFileW(
                devicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_WRITE_THROUGH | NativeMethods.FILE_FLAG_NO_BUFFERING,
                IntPtr.Zero);

            if (hDisk.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                log?.Invoke($"[AVVISO] Impossibile aprire Disco {diskNumber} per reset layout. Win32: {err}");
                return false;
            }

            // 1. Elimina il layout delle partizioni nel kernel per rimuovere qualsiasi vincolo di volume da parte di partmgr
            NativeMethods.DeviceIoControl(
                hDisk,
                NativeMethods.IOCTL_DISK_DELETE_DRIVE_LAYOUT,
                IntPtr.Zero, 0,
                IntPtr.Zero, 0,
                out _,
                IntPtr.Zero);

            // 2. Notifica il kernel per rimuovere tutti gli oggetti volume associati a questo disco
            NativeMethods.DeviceIoControl(
                hDisk,
                NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0,
                IntPtr.Zero, 0,
                out _,
                IntPtr.Zero);

            // 3. Azzeramento dei primi 4 MB (MBR/GPT primario)
            int zeroSize = 4 * 1024 * 1024;
            IntPtr pZero = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)zeroSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pZero != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.ZeroMemory(pZero, (IntPtr)zeroSize);
                    NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                    NativeMethods.WriteFile(hDisk, pZero, (uint)zeroSize, out _, IntPtr.Zero);
                    NativeMethods.FlushFileBuffers(hDisk);
                }
                finally
                {
                    NativeMethods.VirtualFree(pZero, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }
            }

            // 4. Aggiorna nuovamente le proprietà nel kernel
            NativeMethods.DeviceIoControl(
                hDisk,
                NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0,
                IntPtr.Zero, 0,
                out _,
                IntPtr.Zero);

            log?.Invoke($"[OK] Disco {diskNumber} sbloccato e pronto per la copia raw.");
            return true;
        }

        public static bool ConvertToGpt(int diskNumber, Action<string>? log = null)
        {
            log?.Invoke($"Conversione in GPT per Disco {diskNumber} tramite diskpart...");
            string tempScript = Path.Combine(Path.GetTempPath(), $"convert_gpt_{diskNumber}.txt");

            try
            {
                File.WriteAllText(tempScript, $"select disk {diskNumber}\nclean\nconvert gpt\nexit\n");

                var psi = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = $"/s \"{tempScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode == 0 && output.Contains("DiskPart ha convertito il disco"))
                {
                    log?.Invoke($"[OK] Disco {diskNumber} convertito con successo in GPT.");
                    return true;
                }
                else
                {
                    log?.Invoke($"[ESITO] Operazione diskpart terminata su Disco {diskNumber}. Dettagli: {output.Trim()}");
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ERRORE] Conversione in GPT fallita su Disco {diskNumber}: {ex.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(tempScript))
                {
                    try { File.Delete(tempScript); } catch { }
                }
            }
        }

        #region Espansione Automatica Partizione Windows (Destinazione > Sorgente)

        public class GptEntryInfo
        {
            public int Index { get; set; }
            public int PartitionNumber => Index + 1;
            public Guid TypeGuid { get; set; }
            public Guid UniqueGuid { get; set; }
            public long StartingLba { get; set; }
            public long EndingLba { get; set; }
            public ulong Attributes { get; set; }
            public long SectorCount => EndingLba >= StartingLba ? EndingLba - StartingLba + 1 : 0;
            public long SizeBytes => SectorCount * 512;
        }

        public static class Crc32
        {
            private static readonly uint[] Table;

            static Crc32()
            {
                Table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint entry = i;
                    for (int j = 0; j < 8; j++)
                    {
                        if ((entry & 1) == 1)
                            entry = (entry >> 1) ^ 0xEDB88320u;
                        else
                            entry >>= 1;
                    }
                    Table[i] = entry;
                }
            }

            public static uint Compute(byte[] buffer, int offset, int length)
            {
                uint crc = 0xFFFFFFFFu;
                for (int i = offset; i < offset + length; i++)
                {
                    byte tableIndex = (byte)((crc & 0xFF) ^ buffer[i]);
                    crc = (crc >> 8) ^ Table[tableIndex];
                }
                return ~crc;
            }
        }

        public static bool AutoExpandWindowsPartition(int diskNumber, long targetSizeBytes, bool isGpt = true, Action<string>? log = null)
        {
            return FinalizeClonedDisk(diskNumber, needExtend: true, targetSizeBytes: targetSizeBytes, isGptHint: isGpt, log: log);
        }

        private static bool PrepareGptExpansionRaw(int diskNumber, long targetSizeBytes, out bool hasMovedRecovery, Action<string>? log = null)
        {
            hasMovedRecovery = false;
            string devPath = $@"\\.\PhysicalDrive{diskNumber}";
            uint sectorSize = 512;

            byte[] mbrAndGpt = new byte[65536]; // 64 KB buffer (128 settori)
            IntPtr pBuffer = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)mbrAndGpt.Length,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pBuffer == IntPtr.Zero)
            {
                log?.Invoke($"[ERRORE] Allocazione memoria per riparazione GPT fallita su Disco {diskNumber}.");
                return false;
            }

            try
            {
                using (SafeFileHandle hDisk = NativeMethods.CreateFileW(
                    devPath,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_WRITE_THROUGH | NativeMethods.FILE_FLAG_NO_BUFFERING,
                    IntPtr.Zero))
                {
                    if (hDisk.IsInvalid)
                    {
                        int err = Marshal.GetLastWin32Error();
                        log?.Invoke($"[ERRORE] Impossibile aprire Disco {diskNumber} per riparazione GPT. Win32: {err}");
                        return false;
                    }

                    // 1. Leggi primi 64 KB
                    NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                    if (!NativeMethods.ReadFile(hDisk, pBuffer, (uint)mbrAndGpt.Length, out uint bytesRead, IntPtr.Zero) || bytesRead < mbrAndGpt.Length)
                    {
                        int err = Marshal.GetLastWin32Error();
                        log?.Invoke($"[ERRORE] Lettura intestazione GPT fallita su Disco {diskNumber}. Win32: {err}");
                        return false;
                    }

                    Marshal.Copy(pBuffer, mbrAndGpt, 0, mbrAndGpt.Length);

                    // Verifica firma "EFI PART" a offset sectorSize (512)
                    int gptHdrOffset = (int)sectorSize;
                    ulong signature = BitConverter.ToUInt64(mbrAndGpt, gptHdrOffset);
                    if (signature != 0x5452415020494645UL) // "EFI PART"
                    {
                        log?.Invoke($"Disco {diskNumber} non in formato GPT (MBR rilevato). Estensione gestita in Step B.");
                        return true;
                    }

                    uint headerSize = BitConverter.ToUInt32(mbrAndGpt, gptHdrOffset + 0x0C);
                    if (headerSize < 92) headerSize = 92;

                    long partEntryLba = BitConverter.ToInt64(mbrAndGpt, gptHdrOffset + 0x48);
                    int numEntries = BitConverter.ToInt32(mbrAndGpt, gptHdrOffset + 0x50);
                    int sizeEntry = BitConverter.ToInt32(mbrAndGpt, gptHdrOffset + 0x54);

                    if (numEntries <= 0 || sizeEntry < 128)
                    {
                        log?.Invoke($"[ERRORE] Tabella partizioni non valida su Disco {diskNumber}.");
                        return false;
                    }

                    long totalSectors = targetSizeBytes / sectorSize;
                    long lastLba = totalSectors - 1;
                    int tableBytes = numEntries * sizeEntry;
                    long tableSectors = (tableBytes + sectorSize - 1) / sectorSize;
                    long newLastUsableLba = lastLba - tableSectors - 1;

                    int partTableOffset = (int)(partEntryLba * sectorSize);

                    // 2. Analizza le partizioni esistenti
                    var activePartitions = new List<GptEntryInfo>();
                    for (int i = 0; i < numEntries; i++)
                    {
                        int entryOff = partTableOffset + (i * sizeEntry);
                        if (entryOff + sizeEntry > mbrAndGpt.Length) break;

                        byte[] typeGuidBytes = new byte[16];
                        Array.Copy(mbrAndGpt, entryOff, typeGuidBytes, 0, 16);
                        Guid typeGuid = new Guid(typeGuidBytes);

                        if (typeGuid == Guid.Empty) continue;

                        byte[] uniqueGuidBytes = new byte[16];
                        Array.Copy(mbrAndGpt, entryOff + 0x10, uniqueGuidBytes, 0, 16);
                        Guid uniqueGuid = new Guid(uniqueGuidBytes);

                        long startLba = BitConverter.ToInt64(mbrAndGpt, entryOff + 0x20);
                        long endLba = BitConverter.ToInt64(mbrAndGpt, entryOff + 0x28);
                        ulong attributes = BitConverter.ToUInt64(mbrAndGpt, entryOff + 0x30);

                        if (startLba > 0 && endLba >= startLba)
                        {
                            activePartitions.Add(new GptEntryInfo
                            {
                                Index = i,
                                TypeGuid = typeGuid,
                                UniqueGuid = uniqueGuid,
                                StartingLba = startLba,
                                EndingLba = endLba,
                                Attributes = attributes
                            });
                        }
                    }

                    if (activePartitions.Count == 0)
                    {
                        log?.Invoke($"[AVVISO] Nessuna partizione attiva trovata su Disco {diskNumber}.");
                        return false;
                    }

                    // Trova partizione Windows (Basic Data) più grande
                    var windowsPart = activePartitions
                        .Where(p => p.TypeGuid == GptBasicDataGuid)
                        .OrderByDescending(p => p.SectorCount)
                        .FirstOrDefault() ?? activePartitions.OrderByDescending(p => p.SectorCount).First();

                    // 3. Verifica presenza partizione di Ripristino (WinRE) dopo Windows
                    var recoveryPart = activePartitions
                        .FirstOrDefault(p => p.StartingLba > windowsPart.EndingLba &&
                                             p.TypeGuid != GptEfiSystemGuid &&
                                             p.TypeGuid != GptMsrGuid &&
                                             (p.TypeGuid == GptWinReGuid || p.SizeBytes <= 2L * 1024L * 1024L * 1024L));

                    if (recoveryPart != null)
                    {
                        long recSectorCount = recoveryPart.SectorCount;
                        long sectorsPerMB = (1024 * 1024) / sectorSize; // 2048
                        long newRecStartLba = ((newLastUsableLba - recSectorCount + 1) / sectorsPerMB) * sectorsPerMB;
                        long newRecEndLba = newRecStartLba + recSectorCount - 1;

                        if (newRecStartLba > recoveryPart.EndingLba && newRecEndLba <= newLastUsableLba)
                        {
                            double recMB = recoveryPart.SizeBytes / (1024.0 * 1024.0);
                            log?.Invoke($"Rilevata partizione di Ripristino (WinRE) da {recMB:F0} MB dopo Windows. Spostamento a fine disco (settore {newRecStartLba})...");

                            bool copyOk = CopyRawSectors(hDisk, recoveryPart.StartingLba, newRecStartLba, recSectorCount, sectorSize, log);
                            if (!copyOk)
                            {
                                log?.Invoke($"[ERRORE] Copia settori ripristino fallita su Disco {diskNumber}.");
                                return false;
                            }

                            int recEntryOffset = partTableOffset + (recoveryPart.Index * sizeEntry);
                            byte[] winReBytes = GptWinReGuid.ToByteArray();
                            Array.Copy(winReBytes, 0, mbrAndGpt, recEntryOffset, 16);
                            Array.Copy(BitConverter.GetBytes(newRecStartLba), 0, mbrAndGpt, recEntryOffset + 0x20, 8);
                            Array.Copy(BitConverter.GetBytes(newRecEndLba), 0, mbrAndGpt, recEntryOffset + 0x28, 8);
                            ulong winReAttr = 0x8000000000000001UL;
                            Array.Copy(BitConverter.GetBytes(winReAttr), 0, mbrAndGpt, recEntryOffset + 0x30, 8);

                            hasMovedRecovery = true;
                        }
                    }
                    else
                    {
                        log?.Invoke($"Nessuna partizione di ripristino rilevata dopo Windows su Disco {diskNumber}.");
                    }

                    // 4. Calcola nuovo CRC tabella partizioni
                    uint newTableCrc = Crc32.Compute(mbrAndGpt, partTableOffset, tableBytes);

                    // Aggiorna Primary Header
                    Array.Copy(BitConverter.GetBytes((ulong)lastLba), 0, mbrAndGpt, gptHdrOffset + 0x20, 8);
                    Array.Copy(BitConverter.GetBytes((ulong)newLastUsableLba), 0, mbrAndGpt, gptHdrOffset + 0x30, 8);
                    Array.Copy(BitConverter.GetBytes(newTableCrc), 0, mbrAndGpt, gptHdrOffset + 0x58, 4);
                    Array.Clear(mbrAndGpt, gptHdrOffset + 0x10, 4);
                    uint newPrimaryHdrCrc = Crc32.Compute(mbrAndGpt, gptHdrOffset, (int)headerSize);
                    Array.Copy(BitConverter.GetBytes(newPrimaryHdrCrc), 0, mbrAndGpt, gptHdrOffset + 0x10, 4);

                    // Aggiorna MBR protettivo (LBA 0)
                    uint mbrSectorCount = (uint)Math.Min(0xFFFFFFFFL, totalSectors - 1);
                    Array.Copy(BitConverter.GetBytes(mbrSectorCount), 0, mbrAndGpt, 0x1CA, 4);

                    // Scrivi Primary GPT (MBR + Header + Tabella)
                    Marshal.Copy(mbrAndGpt, 0, pBuffer, mbrAndGpt.Length);
                    NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                    NativeMethods.WriteFile(hDisk, pBuffer, (uint)mbrAndGpt.Length, out _, IntPtr.Zero);

                    // 5. Scrivi Backup GPT a fine disco
                    WriteBackupGpt(hDisk, mbrAndGpt, gptHdrOffset, partTableOffset, tableBytes, headerSize, lastLba, tableSectors, newLastUsableLba, newTableCrc, sectorSize, log);

                    NativeMethods.FlushFileBuffers(hDisk);

                    // 6. Notifica driver kernel del nuovo layout
                    NativeMethods.DeviceIoControl(
                        hDisk,
                        NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES,
                        IntPtr.Zero, 0,
                        IntPtr.Zero, 0,
                        out _,
                        IntPtr.Zero);
                }

                Thread.Sleep(300);
                return true;
            }
            finally
            {
                if (pBuffer != IntPtr.Zero)
                {
                    NativeMethods.VirtualFree(pBuffer, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
                }
            }
        }

        private static bool CopyRawSectors(SafeFileHandle hDisk, long srcLba, long dstLba, long sectorCount, uint sectorSize, Action<string>? log)
        {
            int chunkSize = 4 * 1024 * 1024; // 4 MB
            IntPtr pChunk = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)chunkSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pChunk == IntPtr.Zero) return false;

            try
            {
                long sectorsPerChunk = chunkSize / sectorSize;
                long copied = 0;

                while (copied < sectorCount)
                {
                    long remaining = sectorCount - copied;
                    long toCopy = Math.Min(sectorsPerChunk, remaining);
                    uint bytesToRead = (uint)(toCopy * sectorSize);
                    uint alignedToRead = (bytesToRead + 4095u) & ~4095u;

                    long srcOffset = (srcLba + copied) * sectorSize;
                    long dstOffset = (dstLba + copied) * sectorSize;

                    NativeMethods.SetFilePointerEx(hDisk, srcOffset, out _, 0);
                    if (!NativeMethods.ReadFile(hDisk, pChunk, alignedToRead, out uint read, IntPtr.Zero) || read == 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        log?.Invoke($"[ERRORE] Lettura settori ripristino all'offset {srcOffset}. Win32: {err}");
                        return false;
                    }

                    NativeMethods.SetFilePointerEx(hDisk, dstOffset, out _, 0);
                    if (!NativeMethods.WriteFile(hDisk, pChunk, alignedToRead, out uint written, IntPtr.Zero) || written == 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        log?.Invoke($"[ERRORE] Scrittura settori ripristino all'offset {dstOffset}. Win32: {err}");
                        return false;
                    }

                    copied += toCopy;
                }

                NativeMethods.FlushFileBuffers(hDisk);
                return true;
            }
            finally
            {
                NativeMethods.VirtualFree(pChunk, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }

        private static bool WriteBackupGpt(
            SafeFileHandle hDisk,
            byte[] mbrAndGpt,
            int gptHdrOffset,
            int partTableOffset,
            int tableBytes,
            uint headerSize,
            long lastLba,
            long tableSectors,
            long newLastUsableLba,
            uint newTableCrc,
            uint sectorSize,
            Action<string>? log)
        {
            long backupTableLba = lastLba - tableSectors;
            int backupPayloadSize = tableBytes + (int)sectorSize;
            byte[] backupPayload = new byte[backupPayloadSize];

            // 1. Copia tabella partizioni
            Array.Copy(mbrAndGpt, partTableOffset, backupPayload, 0, tableBytes);

            // 2. Copia header primario e adattalo per il backup
            int backupHdrOffset = tableBytes;
            Array.Copy(mbrAndGpt, gptHdrOffset, backupPayload, backupHdrOffset, (int)sectorSize);

            Array.Copy(BitConverter.GetBytes((ulong)lastLba), 0, backupPayload, backupHdrOffset + 0x18, 8); // MyLBA
            Array.Copy(BitConverter.GetBytes(1UL), 0, backupPayload, backupHdrOffset + 0x20, 8); // AlternateLBA
            Array.Copy(BitConverter.GetBytes((ulong)newLastUsableLba), 0, backupPayload, backupHdrOffset + 0x30, 8); // LastUsableLBA
            Array.Copy(BitConverter.GetBytes((ulong)backupTableLba), 0, backupPayload, backupHdrOffset + 0x48, 8); // PartitionEntryLBA
            Array.Copy(BitConverter.GetBytes(newTableCrc), 0, backupPayload, backupHdrOffset + 0x58, 4); // Array CRC

            Array.Clear(backupPayload, backupHdrOffset + 0x10, 4);
            uint backupHdrCrc = Crc32.Compute(backupPayload, backupHdrOffset, (int)headerSize);
            Array.Copy(BitConverter.GetBytes(backupHdrCrc), 0, backupPayload, backupHdrOffset + 0x10, 4);

            // 3. Scrittura con allineamento a 4096 byte
            long backupStartOffset = backupTableLba * sectorSize;
            long alignedOffset = backupStartOffset & ~4095L;
            int prefixDiff = (int)(backupStartOffset - alignedOffset);
            int totalBytesNeeded = prefixDiff + backupPayloadSize;
            int alignedWriteSize = (totalBytesNeeded + 4095) & ~4095;

            IntPtr pAlloc = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)alignedWriteSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pAlloc == IntPtr.Zero)
            {
                log?.Invoke($"[ERRORE] Fallita allocazione buffer backup GPT.");
                return false;
            }

            try
            {
                NativeMethods.ZeroMemory(pAlloc, (IntPtr)alignedWriteSize);

                NativeMethods.SetFilePointerEx(hDisk, alignedOffset, out _, 0);
                NativeMethods.ReadFile(hDisk, pAlloc, (uint)alignedWriteSize, out _, IntPtr.Zero);

                Marshal.Copy(backupPayload, 0, pAlloc + prefixDiff, backupPayloadSize);

                NativeMethods.SetFilePointerEx(hDisk, alignedOffset, out _, 0);
                bool writeOk = NativeMethods.WriteFile(hDisk, pAlloc, (uint)alignedWriteSize, out uint written, IntPtr.Zero);
                if (!writeOk || written < alignedWriteSize)
                {
                    int err = Marshal.GetLastWin32Error();
                    log?.Invoke($"[ERRORE] Scrittura Backup GPT fallita all'offset {alignedOffset}. Win32: {err}");
                    return false;
                }

                return true;
            }
            finally
            {
                NativeMethods.VirtualFree(pAlloc, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }

        private static readonly Guid GptEfiSystemGuid = new Guid("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
        private static readonly Guid GptBasicDataGuid = new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
        private static readonly Guid GptWinReGuid     = new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
        private static readonly Guid GptMsrGuid       = new Guid("e3c9e316-0b5c-4db8-817d-f92df00215ae");

        public static bool ResetDiskSignatureAndBringOnline(int diskNumber, bool isGpt, Action<string>? log = null)
        {
            return FinalizeClonedDisk(diskNumber, needExtend: false, targetSizeBytes: 0, isGptHint: isGpt, log: log);
        }

        private sealed class KernelPartitionEntry
        {
            public uint PartitionNumber { get; set; }
            public long StartingOffset { get; set; }
            public long PartitionLength { get; set; }
            public NativeMethods.PARTITION_STYLE Style { get; set; }
            public Guid GptPartitionType { get; set; }
            public byte MbrPartitionType { get; set; }
            public bool MbrBootIndicator { get; set; }
        }

        private sealed class KernelDriveLayout
        {
            public NativeMethods.PARTITION_STYLE Style { get; set; }
            public List<KernelPartitionEntry> Partitions { get; } = new();
        }

        public static bool? IsDiskGpt(int diskNumber)
        {
            try
            {
                string devPath = $@"\\.\PhysicalDrive{diskNumber}";
                using SafeFileHandle hDisk = NativeMethods.CreateFileW(
                    devPath,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (hDisk.IsInvalid) return null;

                uint sectorSize = GetSectorSize(hDisk);
                byte[] buf = new byte[Math.Max(8192, (int)sectorSize * 2)];
                NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                if (!NativeMethods.ReadFile(hDisk, buf, (uint)buf.Length, out uint read, IntPtr.Zero) || read < sectorSize + 8)
                {
                    return null;
                }

                ulong sig = BitConverter.ToUInt64(buf, (int)sectorSize);
                return sig == 0x5452415020494645UL; // "EFI PART"
            }
            catch
            {
                return null;
            }
        }

        private static KernelDriveLayout? GetKernelDriveLayout(int diskNumber, Action<string>? log)
        {
            string devPath = $@"\\.\PhysicalDrive{diskNumber}";
            using SafeFileHandle hDisk = NativeMethods.CreateFileW(
                devPath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (hDisk.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                log?.Invoke($"[ERRORE] Impossibile aprire Disco {diskNumber} per IOCTL_DISK_GET_DRIVE_LAYOUT_EX. Win32: {err}");
                return null;
            }

            int entrySize = Marshal.SizeOf<NativeMethods.PARTITION_INFORMATION_EX>(); // 144 byte
            int outSize = NativeMethods.DRIVE_LAYOUT_EX_ENTRIES_OFFSET + (256 * entrySize);
            IntPtr pOut = Marshal.AllocHGlobal(outSize);

            try
            {
                NativeMethods.ZeroMemory(pOut, (IntPtr)outSize);
                if (!NativeMethods.DeviceIoControl(
                    hDisk,
                    NativeMethods.IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                    IntPtr.Zero, 0,
                    pOut, (uint)outSize,
                    out uint bytesReturned,
                    IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    log?.Invoke($"[ERRORE] IOCTL_DISK_GET_DRIVE_LAYOUT_EX fallita su Disco {diskNumber}. Win32: {err}");
                    return null;
                }

                var style = (NativeMethods.PARTITION_STYLE)Marshal.ReadInt32(pOut, 0);
                uint partCount = (uint)Marshal.ReadInt32(pOut, 4);

                var layout = new KernelDriveLayout { Style = style };
                for (int i = 0; i < partCount; i++)
                {
                    int offset = NativeMethods.DRIVE_LAYOUT_EX_ENTRIES_OFFSET + (i * entrySize);
                    if (offset + entrySize > bytesReturned) break;

                    var part = Marshal.PtrToStructure<NativeMethods.PARTITION_INFORMATION_EX>(IntPtr.Add(pOut, offset));
                    if (part.PartitionNumber == 0 || part.PartitionLength <= 0) continue;

                    layout.Partitions.Add(new KernelPartitionEntry
                    {
                        PartitionNumber = part.PartitionNumber,
                        StartingOffset = part.StartingOffset,
                        PartitionLength = part.PartitionLength,
                        Style = part.PartitionStyle,
                        GptPartitionType = part.GptPartitionType,
                        MbrPartitionType = part.MbrPartitionType,
                        MbrBootIndicator = part.MbrBootIndicator
                    });
                }

                return layout;
            }
            finally
            {
                Marshal.FreeHGlobal(pOut);
            }
        }

        private static bool HasDiskpartError(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return true;

            return output.Contains("errore", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("impossibile", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("not enough", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("non è stato", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("insufficiente", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("nessun volume", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("no volume", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RunStepADiskpart(int diskNumber, bool isGpt, Action<string>? log)
        {
            string newId = isGpt
                ? Guid.NewGuid().ToString("B")
                : Random.Shared.Next(0x10000000, 0x7FFFFFFF).ToString("X8");

            log?.Invoke($"[Step A] Cambio firma disco ({newId}), online e rescan su Disco {diskNumber}...");

            string BuildStepAScript(string id) =>
                $"select disk {diskNumber}\n" +
                $"uniqueid disk id={id}\n" +
                $"online disk noerr\n" +
                $"attributes disk clear readonly noerr\n" +
                $"rescan\n" +
                $"exit\n";

            var (exitCode, output) = RunDiskpartScript(BuildStepAScript(newId), $"stepA_{diskNumber}");

            if (exitCode != 0 && isGpt)
            {
                string bareId = Guid.NewGuid().ToString("D");
                var (retryExit, retryOut) = RunDiskpartScript(BuildStepAScript(bareId), $"stepA_bare_{diskNumber}");
                if (retryExit == 0)
                {
                    exitCode = retryExit;
                    output = retryOut;
                    newId = bareId;
                }
            }

            if (exitCode == 0 && !HasDiskpartError(output))
            {
                log?.Invoke($"[OK] Firma Disco {diskNumber} reimpostata ({newId}), disco online e rescan completato.");
            }
            else
            {
                log?.Invoke($"[AVVISO Step A] Esito DiskPart su Disco {diskNumber} (ExitCode {exitCode}):\n{output.Trim()}");
            }

            // Notifica ulteriore al kernel e attesa 2 secondi per aggiornamento tabella volumi in WinPE
            try
            {
                using SafeFileHandle hDisk = NativeMethods.CreateFileW(
                    $@"\\.\PhysicalDrive{diskNumber}",
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (!hDisk.IsInvalid)
                {
                    NativeMethods.DeviceIoControl(hDisk, NativeMethods.IOCTL_DISK_UPDATE_PROPERTIES, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                }
            }
            catch { }

            Thread.Sleep(2000);
            return exitCode == 0;
        }

        private static (char WinLetter, char SysLetter) GetTwoFreeDriveLetters()
        {
            var used = new HashSet<char> { 'A', 'B', 'X' };
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!string.IsNullOrEmpty(d.Name))
                    {
                        used.Add(char.ToUpperInvariant(d.Name[0]));
                    }
                }
                foreach (var d in Environment.GetLogicalDrives())
                {
                    if (!string.IsNullOrEmpty(d))
                    {
                        used.Add(char.ToUpperInvariant(d[0]));
                    }
                }
            }
            catch { }

            char PickFree(char preferred)
            {
                if (!used.Contains(preferred))
                {
                    used.Add(preferred);
                    return preferred;
                }
                for (char c = 'Z'; c >= 'D'; c--)
                {
                    if (!used.Contains(c))
                    {
                        used.Add(c);
                        return c;
                    }
                }
                return preferred;
            }

            return (PickFree('W'), PickFree('S'));
        }

        private static (int ExitCode, string Output) RunDiskpartScript(string scriptContent, string tag)
        {
            string scriptFile = Path.Combine(Path.GetTempPath(), $"dp_{tag}_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(scriptFile, scriptContent);
                var psi = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = $"/s \"{scriptFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return (-1, "Impossibile avviare diskpart.exe");
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                string combined = string.IsNullOrWhiteSpace(stderr)
                    ? stdout
                    : (string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout}\n{stderr}");
                return (proc.ExitCode, combined);
            }
            finally
            {
                if (File.Exists(scriptFile))
                {
                    try { File.Delete(scriptFile); } catch { }
                }
            }
        }

        private static void DeleteExistingBcdFiles(char systemLetter, bool isGpt, Action<string>? log)
        {
            string bootDir = isGpt
                ? $"{systemLetter}:\\EFI\\Microsoft\\Boot"
                : $"{systemLetter}:\\Boot";

            try
            {
                if (!Directory.Exists(bootDir)) return;

                string bcdPath = Path.Combine(bootDir, "BCD");
                if (File.Exists(bcdPath))
                {
                    File.SetAttributes(bcdPath, FileAttributes.Normal);
                    File.Delete(bcdPath);
                    log?.Invoke($"Eliminato vecchio archivio BCD: {bcdPath}");
                }

                foreach (string logFile in Directory.GetFiles(bootDir, "BCD.LOG*"))
                {
                    try
                    {
                        File.SetAttributes(logFile, FileAttributes.Normal);
                        File.Delete(logFile);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[AVVISO] Impossibile eliminare completamente il vecchio BCD in {bootDir}: {ex.Message}");
            }
        }

        private static (int ExitCode, string Output) RunBcdboot(string windowsDir, char systemLetter, bool isGpt)
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string bcdbootPath = Path.Combine(sysDir, "bcdboot.exe");
            if (!File.Exists(bcdbootPath))
            {
                bcdbootPath = "bcdboot.exe";
            }

            string firmware = isGpt ? "UEFI" : "BIOS";

            (int Code, string Out) Exec(string args)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = bcdbootPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return (-1, "Impossibile avviare bcdboot.exe");
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                string combined = string.IsNullOrWhiteSpace(stderr)
                    ? stdout
                    : (string.IsNullOrWhiteSpace(stdout) ? stderr : $"{stdout}\n{stderr}");
                return (proc.ExitCode, combined);
            }

            return Exec($"\"{windowsDir}\" /s {systemLetter}: /f {firmware}");
        }

        private static void CleanupHibernationAndSwapFiles(char winLetter, Action<string>? log)
        {
            foreach (string fileName in new[] { "hiberfil.sys", "swapfile.sys" })
            {
                string fullPath = $"{winLetter}:\\{fileName}";
                try
                {
                    if (File.Exists(fullPath))
                    {
                        File.SetAttributes(fullPath, FileAttributes.Normal);
                        File.Delete(fullPath);
                        log?.Invoke($"[OK] Eliminato {fullPath} (pulizia Avvio Rapido / ibernazione).");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[AVVISO] Impossibile eliminare {fullPath}: {ex.Message}");
                }
            }
        }

        private static void SetBootStatusPolicyIgnoreAllFailures(char systemLetter, bool isGpt, int diskNumber, Action<string>? log)
        {
            string bcdStore = isGpt
                ? $"{systemLetter}:\\EFI\\Microsoft\\Boot\\BCD"
                : $"{systemLetter}:\\Boot\\BCD";

            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string bcdeditPath = Path.Combine(sysDir, "bcdedit.exe");
            if (!File.Exists(bcdeditPath))
            {
                bcdeditPath = "bcdedit.exe";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = bcdeditPath,
                    Arguments = $"/store \"{bcdStore}\" /set {{default}} bootstatuspolicy ignoreallfailures",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    log?.Invoke($"[AVVISO] Impossibile avviare bcdedit.exe su Disco {diskNumber}.");
                    return;
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode == 0)
                {
                    log?.Invoke($"[OK] Impostato bootstatuspolicy ignoreallfailures nel BCD su Disco {diskNumber}.");
                }
                else
                {
                    string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    log?.Invoke($"[AVVISO] bcdedit bootstatuspolicy ha restituito ExitCode {proc.ExitCode} su Disco {diskNumber}: {err.Trim()}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[AVVISO] Eccezione durante bcdedit bootstatuspolicy su Disco {diskNumber}: {ex.Message}");
            }
        }

        public static bool TryTrimEntireDisk(SafeFileHandle hDisk, int diskNumber, long diskSizeBytes, Action<string>? log = null)
        {
            if (hDisk.IsInvalid || diskSizeBytes <= 4096)
            {
                log?.Invoke($"[AVVISO] TRIM non supportato su Disco {diskNumber} (handle o dimensione non valida): Zero-Skip disattivato per integrità dati.");
                return false;
            }

            const int verifySize = 4096;
            IntPtr pVerify = NativeMethods.VirtualAlloc(
                IntPtr.Zero,
                (UIntPtr)verifySize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            if (pVerify == IntPtr.Zero)
            {
                log?.Invoke($"[AVVISO] TRIM non verificabile su Disco {diskNumber} (memoria insufficiente): Zero-Skip disattivato per integrità dati.");
                return false;
            }

            try
            {
                // 1. Scrivi un pattern noto (0x5A) sul primo blocco da 4 KB per verificare se il controller azzera realmente dopo TRIM (RZAT)
                byte[] testPattern = new byte[verifySize];
                Array.Fill(testPattern, (byte)0x5A);
                Marshal.Copy(testPattern, 0, pVerify, verifySize);

                NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                NativeMethods.WriteFile(hDisk, pVerify, verifySize, out _, IntPtr.Zero);
                NativeMethods.FlushFileBuffers(hDisk);

                // 2. Costruisci ed invia IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES (DeviceDsmAction_Trim) sull'intero disco
                long alignedLength = diskSizeBytes & ~4095L;
                int lastErr = 0;
                bool trimIoctlOk = false;

                uint[] flagsToTry =
                {
                    0u,
                    NativeMethods.DEVICE_DSM_FLAG_TRIM_NOT_FS_ALLOCATED,
                    NativeMethods.DEVICE_DSM_FLAG_ENTIRE_DATA_SET_RANGE
                };

                IntPtr pDsm = Marshal.AllocHGlobal(48);
                try
                {
                    foreach (uint flags in flagsToTry)
                    {
                        NativeMethods.ZeroMemory(pDsm, (IntPtr)48);
                        bool entireFlag = (flags == NativeMethods.DEVICE_DSM_FLAG_ENTIRE_DATA_SET_RANGE);

                        Marshal.WriteInt32(pDsm, 0, 28);
                        Marshal.WriteInt32(pDsm, 4, (int)NativeMethods.DeviceDsmAction_Trim);
                        Marshal.WriteInt32(pDsm, 8, unchecked((int)flags));
                        Marshal.WriteInt32(pDsm, 12, 0);
                        Marshal.WriteInt32(pDsm, 16, 0);
                        Marshal.WriteInt32(pDsm, 20, entireFlag ? 0 : 32);
                        Marshal.WriteInt32(pDsm, 24, entireFlag ? 0 : 16);

                        if (!entireFlag)
                        {
                            Marshal.WriteInt64(pDsm, 32, 0L);
                            Marshal.WriteInt64(pDsm, 40, alignedLength);
                        }

                        uint inSize = entireFlag ? 28u : 48u;
                        if (NativeMethods.DeviceIoControl(
                            hDisk,
                            NativeMethods.IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES,
                            pDsm, inSize,
                            IntPtr.Zero, 0,
                            out _,
                            IntPtr.Zero))
                        {
                            trimIoctlOk = true;
                            break;
                        }

                        lastErr = Marshal.GetLastWin32Error();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pDsm);
                }

                if (!trimIoctlOk)
                {
                    NativeMethods.ZeroMemory(pVerify, (IntPtr)verifySize);
                    NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                    NativeMethods.WriteFile(hDisk, pVerify, verifySize, out _, IntPtr.Zero);
                    NativeMethods.FlushFileBuffers(hDisk);

                    log?.Invoke($"[AVVISO] TRIM non supportato su Disco {diskNumber} (Win32: {lastErr}): Zero-Skip disattivato per integrità dati.");
                    return false;
                }

                // 3. Verifica fisica che il disco restituisca effettivamente zeri reali dopo il TRIM (RZAT)
                Array.Fill(testPattern, (byte)0xFF);
                Marshal.Copy(testPattern, 0, pVerify, verifySize);
                NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                bool readOk = NativeMethods.ReadFile(hDisk, pVerify, verifySize, out uint bytesRead, IntPtr.Zero);

                bool isZeroAfterTrim = false;
                if (readOk && bytesRead == verifySize)
                {
                    isZeroAfterTrim = true;
                    for (int off = 0; off < verifySize; off += 8)
                    {
                        if (Marshal.ReadInt64(pVerify, off) != 0L)
                        {
                            isZeroAfterTrim = false;
                            break;
                        }
                    }
                }

                if (isZeroAfterTrim)
                {
                    log?.Invoke($"[OK] TRIM completo eseguito su Disco {diskNumber} (Zero-Skip attivo).");
                    return true;
                }
                else
                {
                    NativeMethods.ZeroMemory(pVerify, (IntPtr)verifySize);
                    NativeMethods.SetFilePointerEx(hDisk, 0, out _, 0);
                    NativeMethods.WriteFile(hDisk, pVerify, verifySize, out _, IntPtr.Zero);
                    NativeMethods.FlushFileBuffers(hDisk);

                    log?.Invoke($"[AVVISO] TRIM non azzera i settori in lettura su Disco {diskNumber}: Zero-Skip disattivato per integrità dati.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[AVVISO] Errore TRIM su Disco {diskNumber} ({ex.Message}): Zero-Skip disattivato per integrità dati.");
                return false;
            }
            finally
            {
                NativeMethods.VirtualFree(pVerify, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }

        public static bool FinalizeClonedDisk(
            int diskNumber,
            bool needExtend,
            long targetSizeBytes,
            bool isGptHint,
            Action<string>? log = null)
        {
            lock (_diskpartLock)
            {
                bool hasMovedRecovery = false;

                // 0. Riparazione GPT di backup + eventuale spostamento WinRE in fondo (se Destinazione > Sorgente)
                if (needExtend && targetSizeBytes > 0)
                {
                    PrepareGptExpansionRaw(diskNumber, targetSizeBytes, out hasMovedRecovery, log);
                }

                bool isGpt = IsDiskGpt(diskNumber) ?? isGptHint;

                // 1. Step A: uniqueid + online + clear readonly + rescan + attesa 2s
                RunStepADiskpart(diskNumber, isGpt, log);

                // 2. Lettura dinamica reale delle partizioni tramite IOCTL_DISK_GET_DRIVE_LAYOUT_EX (indici 1-based)
                var layout = GetKernelDriveLayout(diskNumber, log);
                if (layout == null || layout.Partitions.Count == 0)
                {
                    log?.Invoke($"[ERRORE] Impossibile leggere le partizioni dal kernel per Disco {diskNumber} dopo rescan.");
                    return false;
                }

                bool layoutIsGpt = (layout.Style == NativeMethods.PARTITION_STYLE.PARTITION_STYLE_GPT);
                uint efiPartNum = 0;
                uint mbrActivePartNum = 0;
                List<KernelPartitionEntry> winCandidates;

                if (layoutIsGpt)
                {
                    var efiPart = layout.Partitions.FirstOrDefault(p => p.GptPartitionType == GptEfiSystemGuid);
                    if (efiPart != null)
                    {
                        efiPartNum = efiPart.PartitionNumber;
                    }

                    winCandidates = layout.Partitions
                        .Where(p => p.GptPartitionType == GptBasicDataGuid && p.PartitionLength >= 1024L * 1024L * 1024L)
                        .OrderByDescending(p => p.PartitionLength)
                        .ToList();

                    if (winCandidates.Count == 0)
                    {
                        winCandidates = layout.Partitions
                            .Where(p => p.GptPartitionType != GptEfiSystemGuid &&
                                        p.GptPartitionType != GptMsrGuid &&
                                        p.GptPartitionType != GptWinReGuid &&
                                        p.PartitionLength >= 1024L * 1024L * 1024L)
                            .OrderByDescending(p => p.PartitionLength)
                            .ToList();
                    }
                }
                else
                {
                    var activePart = layout.Partitions.FirstOrDefault(p => p.MbrBootIndicator);
                    if (activePart != null)
                    {
                        mbrActivePartNum = activePart.PartitionNumber;
                    }

                    winCandidates = layout.Partitions
                        .Where(p => p.MbrPartitionType != 0x05 &&
                                    p.MbrPartitionType != 0x0F &&
                                    p.MbrPartitionType != 0x27 &&
                                    p.PartitionLength >= 1024L * 1024L * 1024L)
                        .OrderByDescending(p => p.PartitionLength)
                        .ToList();
                }

                var primaryWinPart = winCandidates.FirstOrDefault();
                if (primaryWinPart == null)
                {
                    log?.Invoke($"[ERRORE] Nessuna partizione Windows candidata trovata tramite IOCTL su Disco {diskNumber}.");
                    return false;
                }

                uint winPartNum = primaryWinPart.PartitionNumber;
                long winSizeBefore = primaryWinPart.PartitionLength;

                log?.Invoke(layoutIsGpt
                    ? $"[Step B] Indici GPT rilevati su Disco {diskNumber}: Windows=Partizione {winPartNum}, EFI={(efiPartNum > 0 ? $"Partizione {efiPartNum}" : "NON TROVATA")}."
                    : $"[Step B] Indici MBR rilevati su Disco {diskNumber}: Windows=Partizione {winPartNum}, Attiva={(mbrActivePartNum > 0 ? $"Partizione {mbrActivePartNum}" : "Nessuna")}.");

                var (winLetter, sysLetter) = GetTwoFreeDriveLetters();
                bool assignedWinLetter = false;
                bool assignedSysLetter = false;

                try
                {
                    // 3. Step B: Extend (se richiesto) + assegnazione lettere in una seconda chiamata diskpart separata
                    var stepBSb = new System.Text.StringBuilder();
                    stepBSb.AppendLine($"select disk {diskNumber}");
                    stepBSb.AppendLine($"select partition {winPartNum}");
                    if (needExtend)
                    {
                        stepBSb.AppendLine("extend");
                    }
                    else if (!layoutIsGpt && mbrActivePartNum == 0)
                    {
                        stepBSb.AppendLine("active");
                    }
                    stepBSb.AppendLine("remove all noerr");
                    stepBSb.AppendLine($"assign letter={winLetter} noerr");

                    if (layoutIsGpt && efiPartNum > 0)
                    {
                        stepBSb.AppendLine($"select partition {efiPartNum}");
                        stepBSb.AppendLine("remove all noerr");
                        stepBSb.AppendLine($"assign letter={sysLetter} noerr");
                        assignedSysLetter = true;
                    }
                    else if (!layoutIsGpt && mbrActivePartNum > 0 && mbrActivePartNum != winPartNum)
                    {
                        stepBSb.AppendLine($"select partition {mbrActivePartNum}");
                        stepBSb.AppendLine("remove all noerr");
                        stepBSb.AppendLine($"assign letter={sysLetter} noerr");
                        assignedSysLetter = true;
                    }
                    stepBSb.AppendLine("exit");

                    assignedWinLetter = true;
                    var (stepBExit, stepBOut) = RunDiskpartScript(stepBSb.ToString(), $"stepB_{diskNumber}");
                    Thread.Sleep(500);

                    // 4. Verifica reale dell'estensione (output DiskPart + dimensione effettiva dal kernel)
                    if (needExtend)
                    {
                        var layoutAfter = GetKernelDriveLayout(diskNumber, null);
                        long winSizeAfter = layoutAfter?.Partitions
                            .FirstOrDefault(p => p.PartitionNumber == winPartNum)?.PartitionLength ?? winSizeBefore;

                        bool sizeGrew = winSizeAfter > winSizeBefore + (64L * 1024L * 1024L);
                        bool hasError = stepBExit != 0 || HasDiskpartError(stepBOut);

                        double beforeGB = winSizeBefore / (1024.0 * 1024.0 * 1024.0);
                        double afterGB = winSizeAfter / (1024.0 * 1024.0 * 1024.0);

                        if (sizeGrew && !hasError)
                        {
                            log?.Invoke(hasMovedRecovery
                                ? $"[OK] Partizione Windows estesa con successo su Disco {diskNumber}: {beforeGB:F1} GB -> {afterGB:F1} GB (WinRE spostata in fondo)."
                                : $"[OK] Partizione Windows estesa con successo su Disco {diskNumber}: {beforeGB:F1} GB -> {afterGB:F1} GB.");
                        }
                        else
                        {
                            log?.Invoke($"[ERRORE ESTENSIONE] Estensione partizione Windows fallita o non verificata su Disco {diskNumber} (Prima: {beforeGB:F1} GB, Dopo: {afterGB:F1} GB).\nOutput completo DiskPart:\n{stepBOut.Trim()}");
                        }
                    }

                    // 5. Verifica effettiva dei volumi montati (W:\Windows e S:\)
                    if (!Directory.Exists($"{winLetter}:\\"))
                    {
                        RunDiskpartScript(
                            $"rescan\nselect disk {diskNumber}\nselect partition {winPartNum}\nremove all noerr\nassign letter={winLetter} noerr\nexit\n",
                            $"retry_win_{diskNumber}");
                        Thread.Sleep(1000);
                    }

                    if (Directory.Exists($"{winLetter}:\\") && !Directory.Exists($"{winLetter}:\\Windows") && winCandidates.Count > 1)
                    {
                        foreach (var alt in winCandidates.Skip(1))
                        {
                            RunDiskpartScript(
                                $"select volume {winLetter}\nremove letter={winLetter} noerr\nselect disk {diskNumber}\nselect partition {alt.PartitionNumber}\nremove all noerr\nassign letter={winLetter} noerr\nexit\n",
                                $"alt_win_{diskNumber}");
                            Thread.Sleep(500);
                            if (Directory.Exists($"{winLetter}:\\Windows"))
                            {
                                winPartNum = alt.PartitionNumber;
                                break;
                            }
                        }
                    }

                    if (!Directory.Exists($"{winLetter}:\\Windows"))
                    {
                        log?.Invoke($"[ERRORE] Cartella {winLetter}:\\Windows non trovata su Disco {diskNumber} (Partizione {winPartNum}). Impossibile aggiornare il BCD.");
                        return false;
                    }

                    char effectiveSysLetter;
                    if (layoutIsGpt)
                    {
                        if (efiPartNum == 0)
                        {
                            log?.Invoke($"[ERRORE] Partizione di sistema EFI (c12a7328-f81f-11d2-ba4b-00a0c93ec93b) non trovata su Disco {diskNumber} (GPT). Impossibile rigenerare il BCD.");
                            return false;
                        }

                        if (!Directory.Exists($"{sysLetter}:\\"))
                        {
                            RunDiskpartScript(
                                $"rescan\nselect disk {diskNumber}\nselect partition {efiPartNum}\nremove all noerr\nassign letter={sysLetter} noerr\nexit\n",
                                $"retry_efi_{diskNumber}");
                            Thread.Sleep(1000);
                        }

                        if (!Directory.Exists($"{sysLetter}:\\"))
                        {
                            log?.Invoke($"[ERRORE] Root EFI {sysLetter}:\\ non accessibile su Disco {diskNumber} (Partizione {efiPartNum}). Impossibile rigenerare il BCD.");
                            return false;
                        }

                        effectiveSysLetter = sysLetter;
                    }
                    else
                    {
                        if (mbrActivePartNum > 0 && mbrActivePartNum != winPartNum)
                        {
                            if (!Directory.Exists($"{sysLetter}:\\"))
                            {
                                RunDiskpartScript(
                                    $"rescan\nselect disk {diskNumber}\nselect partition {mbrActivePartNum}\nremove all noerr\nassign letter={sysLetter} noerr\nexit\n",
                                    $"retry_mbrsys_{diskNumber}");
                                Thread.Sleep(1000);
                            }

                            if (!Directory.Exists($"{sysLetter}:\\"))
                            {
                                log?.Invoke($"[ERRORE] Partizione attiva {sysLetter}:\\ non accessibile su Disco {diskNumber}.");
                                return false;
                            }
                            effectiveSysLetter = sysLetter;
                        }
                        else
                        {
                            effectiveSysLetter = winLetter;
                        }
                    }

                    // 6. Pulizia file di Avvio Rapido/Ibernazione + eliminazione vecchio BCD + bcdboot.exe + bootstatuspolicy
                    CleanupHibernationAndSwapFiles(winLetter, log);
                    DeleteExistingBcdFiles(effectiveSysLetter, layoutIsGpt, log);

                    string winDirPath = $"{winLetter}:\\Windows";
                    string fwMode = layoutIsGpt ? "UEFI" : "BIOS";
                    log?.Invoke($"Esecuzione bcdboot.exe {winDirPath} /s {effectiveSysLetter}: /f {fwMode} su Disco {diskNumber}...");

                    var (bcdExit, bcdOut) = RunBcdboot(winDirPath, effectiveSysLetter, layoutIsGpt);
                    if (bcdExit == 0)
                    {
                        log?.Invoke($"[OK] Bootloader {fwMode} (BCD) aggiornato con successo su Disco {diskNumber}.");
                        SetBootStatusPolicyIgnoreAllFailures(effectiveSysLetter, layoutIsGpt, diskNumber, log);
                        return true;
                    }
                    else
                    {
                        string errDetail = string.IsNullOrWhiteSpace(bcdOut) ? $"ExitCode {bcdExit}" : bcdOut.Trim();
                        log?.Invoke($"[ERRORE] bcdboot.exe fallito su Disco {diskNumber} (ExitCode {bcdExit}): {errDetail}");
                        return false;
                    }
                }
                finally
                {
                    // 7. Rimozione di entrambe le lettere temporanee (S: e W:) tramite diskpart
                    var cleanupSb = new System.Text.StringBuilder();
                    if (assignedSysLetter)
                    {
                        cleanupSb.AppendLine($"select volume {sysLetter}");
                        cleanupSb.AppendLine($"remove letter={sysLetter} noerr");
                    }
                    if (assignedWinLetter)
                    {
                        cleanupSb.AppendLine($"select volume {winLetter}");
                        cleanupSb.AppendLine($"remove letter={winLetter} noerr");
                    }
                    if (cleanupSb.Length > 0)
                    {
                        cleanupSb.AppendLine("exit");
                        RunDiskpartScript(cleanupSb.ToString(), $"cleanup_letters_{diskNumber}");
                    }
                }
            }
        }

        public static bool RepairBcdOnTargetDisk(int diskNumber, Action<string>? log = null)
        {
            return FinalizeClonedDisk(diskNumber, needExtend: false, targetSizeBytes: 0, isGptHint: true, log: log);
        }

        #endregion
    }
}
