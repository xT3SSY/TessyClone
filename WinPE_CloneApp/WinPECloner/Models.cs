using System;
using System.Collections.Generic;

namespace WinPECloner
{
    public class DiskInfo
    {
        public int DiskNumber { get; set; }
        public string DevicePath => $@"\\.\PhysicalDrive{DiskNumber}";
        public string Model { get; set; } = "Disco sconosciuto";
        public string SerialNumber { get; set; } = "";
        public string BusType { get; set; } = "Sconosciuto";
        public long TotalSizeBytes { get; set; }
        public long UsedSizeBytes { get; set; }
        public long PhysicalPartitionsAllocatedBytes { get; set; }
        public string PhysicalPartitionSummary { get; set; } = "";
        public uint SectorSize { get; set; } = 512;
        public bool IsOffline { get; set; }
        public NativeMethods.PARTITION_STYLE PartitionStyle { get; set; } = NativeMethods.PARTITION_STYLE.PARTITION_STYLE_RAW;
        public List<string> Volumes { get; set; } = new List<string>();

        public double TotalSizeGB => Math.Round((double)TotalSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);
        public double UsedSizeGB => Math.Round((double)UsedSizeBytes / (1024.0 * 1024.0 * 1024.0), 2);

        public double FillRatio
        {
            get
            {
                if (TotalSizeBytes <= 0 || UsedSizeBytes <= 0) return 0.0;
                return Math.Clamp((double)UsedSizeBytes / TotalSizeBytes, 0.0, 1.0);
            }
        }

        public string CleanCapacity => FormatCleanSize(TotalSizeBytes);
        public string CleanUsed => UsedSizeBytes > 0 ? FormatCleanSize(UsedSizeBytes) : "0 GB";

        public string PartitionStyleName => PartitionStyle switch
        {
            NativeMethods.PARTITION_STYLE.PARTITION_STYLE_GPT => "GPT",
            NativeMethods.PARTITION_STYLE.PARTITION_STYLE_MBR => "MBR",
            _ => "Non inizializzato (RAW)"
        };

        public string DisplayName => $"[Disco {DiskNumber}] {Model} ({CleanCapacity} — {PartitionStyleName}) — Bus: {BusType}";

        public string VolumeSummary
        {
            get
            {
                if (Volumes.Count > 0)
                {
                    return string.Join(", ", Volumes);
                }
                if (!string.IsNullOrEmpty(PhysicalPartitionSummary))
                {
                    return UsedSizeBytes > 0
                        ? $"{PhysicalPartitionSummary} — {FormatCleanSize(UsedSizeBytes)}/{CleanCapacity} usati"
                        : PhysicalPartitionSummary;
                }
                return "(Nessuna partizione rilevata)";
            }
        }

        public static string FormatCleanSize(long bytes)
        {
            if (bytes <= 0) return "0 GB";
            double gb = (double)bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1000.0)
            {
                double tb = gb / 1024.0;
                return tb == Math.Floor(tb) ? $"{tb:F0} TB" : $"{tb:F1} TB";
            }
            return $"{Math.Round(gb):F0} GB";
        }
    }

    public class CloneProgressReport
    {
        public long BytesCopied { get; set; }
        public long TotalBytes { get; set; }
        private double? _percentage;
        public double Percentage
        {
            get => _percentage ?? (TotalBytes > 0 ? (double)BytesCopied / TotalBytes * 100.0 : 0.0);
            set => _percentage = value;
        }
        public double SpeedMBs { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan EstimatedRemaining { get; set; }
        public string? PhaseMessage { get; set; }
        public Dictionary<int, string> TargetStatuses { get; set; } = new Dictionary<int, string>();
    }
}
