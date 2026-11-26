using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinPECloner
{
    public static class NativeMethods
    {
        #region IOCTL & FSCTL Constants
        public const uint IOCTL_STORAGE_QUERY_PROPERTY               = 0x002D1400;
        public const uint IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES   = 0x002D9404; // CTL_CODE(0x2d, 0x0501, METHOD_BUFFERED, FILE_WRITE_ACCESS)
        public const uint DeviceDsmAction_Trim                       = 1;
        public const uint DEVICE_DSM_FLAG_ENTIRE_DATA_SET_RANGE      = 0x00000001;
        public const uint DEVICE_DSM_FLAG_TRIM_NOT_FS_ALLOCATED      = 0x80000000;
        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX           = 0x000700A0;
        public const uint IOCTL_DISK_GET_LENGTH_INFO            = 0x0007405C; // CTL_CODE(7, 0x0017, METHOD_BUFFERED, FILE_READ_ACCESS)
        public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX        = 0x00070050;
        public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX        = 0x0007C054;
        public const uint IOCTL_DISK_DELETE_DRIVE_LAYOUT        = 0x0007C040; // CTL_CODE(7, 0x0010, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
        public const uint IOCTL_DISK_CREATE_DISK                = 0x00070058;
        public const uint IOCTL_DISK_UPDATE_PROPERTIES          = 0x00070140;

        public const uint FSCTL_LOCK_VOLUME                     = 0x00090018;
        public const uint FSCTL_UNLOCK_VOLUME                   = 0x0009001C;
        public const uint FSCTL_DISMOUNT_VOLUME                 = 0x00090020;
        public const uint FSCTL_IS_VOLUME_MOUNTED               = 0x00090028;
        public const uint FSCTL_ALLOW_EXTENDED_DASD_IO          = 0x00090083; // CTL_CODE(9, 32, METHOD_NEITHER, FILE_ANY_ACCESS)

        public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS  = 0x00560000;
        public const uint IOCTL_VOLUME_OFFLINE                  = 0x00560010;
        #endregion

        #region File Access & Flags
        public const uint GENERIC_READ                          = 0x80000000;
        public const uint GENERIC_WRITE                         = 0x40000000;
        public const uint FILE_SHARE_READ                       = 0x00000001;
        public const uint FILE_SHARE_WRITE                      = 0x00000002;
        public const uint OPEN_EXISTING                         = 3;
        public const uint FILE_FLAG_NO_BUFFERING                = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH               = 0x80000000;
        public const uint FILE_FLAG_SEQUENTIAL_SCAN             = 0x08000000;
        #endregion

        #region Memory Management (Allineamento Buffer 4096 / Page)
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
        public static extern void ZeroMemory(IntPtr dest, IntPtr size);

        public const uint MEM_COMMIT      = 0x00001000;
        public const uint MEM_RESERVE     = 0x00002000;
        public const uint MEM_RELEASE     = 0x00008000;
        public const uint PAGE_READWRITE  = 0x04;
        #endregion

        #region Structs
        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] AdditionalParameters;
        }

        public enum STORAGE_BUS_TYPE : int
        {
            BusTypeUnknown = 0x00,
            BusTypeScsi = 0x1,
            BusTypeAtapi = 0x2,
            BusTypeAta = 0x3,
            BusType1394 = 0x4,
            BusTypeSsa = 0x5,
            BusTypeFibre = 0x6,
            BusTypeUsb = 0x7,
            BusTypeRAID = 0x8,
            BusTypeiScsi = 0x9,
            BusTypeSas = 0xA,
            BusTypeSata = 0xB,
            BusTypeSd = 0xC,
            BusTypeMmc = 0xD,
            BusTypeVirtual = 0xE,
            BusTypeFileBackedVirtual = 0xF,
            BusTypeSpaces = 0x10,
            BusTypeNvme = 0x11,
            BusTypeSCM = 0x12,
            BusTypeUfs = 0x13,
            BusTypeMax = 0x14
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            public byte RemovableMedia;
            public byte CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public STORAGE_BUS_TYPE BusType;
            public uint RawPropertiesLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] RawDeviceProperties;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GET_LENGTH_INFORMATION
        {
            public long Length; // 8 bytes esatti
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY
        {
            public long Cylinders;
            public int MediaType;
            public uint TracksPerCylinder;
            public uint SectorsPerTrack;
            public uint BytesPerSector;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY_EX
        {
            public DISK_GEOMETRY Geometry;
            public long DiskSize;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] Data;
        }

        public enum PARTITION_STYLE : uint
        {
            PARTITION_STYLE_MBR = 0,
            PARTITION_STYLE_GPT = 1,
            PARTITION_STYLE_RAW = 2
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DRIVE_LAYOUT_INFORMATION_EX
        {
            public PARTITION_STYLE PartitionStyle;
            public uint PartitionCount;
            // Dati MBR/GPT unions a seguire (offset 8..47) -> PartitionEntry[0] inizia a offset 48
        }

        public const int DRIVE_LAYOUT_EX_ENTRIES_OFFSET = 48;

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        public struct PARTITION_INFORMATION_EX
        {
            [FieldOffset(0)]  public PARTITION_STYLE PartitionStyle;
            [FieldOffset(8)]  public long StartingOffset;
            [FieldOffset(16)] public long PartitionLength;
            [FieldOffset(24)] public uint PartitionNumber;
            [FieldOffset(28)] [MarshalAs(UnmanagedType.U1)] public bool RewritePartition;
            [FieldOffset(29)] [MarshalAs(UnmanagedType.U1)] public bool IsServicePartition;

            // Unione MBR (offset 32)
            [FieldOffset(32)] public byte MbrPartitionType;
            [FieldOffset(33)] [MarshalAs(UnmanagedType.U1)] public bool MbrBootIndicator;
            [FieldOffset(34)] [MarshalAs(UnmanagedType.U1)] public bool MbrRecognizedPartition;
            [FieldOffset(36)] public uint MbrHiddenSectors;

            // Unione GPT (offset 32)
            [FieldOffset(32)] public Guid GptPartitionType;
            [FieldOffset(48)] public Guid GptPartitionId;
            [FieldOffset(64)] public ulong GptAttributes;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct CREATE_DISK
        {
            [FieldOffset(0)]
            public PARTITION_STYLE PartitionStyle;
            [FieldOffset(4)]
            public uint MbrSignature;
            [FieldOffset(4)]
            public Guid GptDiskId;
            [FieldOffset(20)]
            public uint MaxPartitionCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_EXTENT
        {
            public uint DiskNumber;
            public long StartingOffset;
            public long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VOLUME_DISK_EXTENTS
        {
            public uint NumberOfDiskExtents;
            public DISK_EXTENT Extent;
        }
        #endregion

        #region P/Invoke Imports
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            SafeFileHandle hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(
            SafeFileHandle hFile,
            [Out] byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            SafeFileHandle hFile,
            IntPtr lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindFirstVolumeW(
            [Out] System.Text.StringBuilder lpszVolumeName,
            uint cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextVolumeW(
            IntPtr hFindVolume,
            [Out] System.Text.StringBuilder lpszVolumeName,
            uint cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindVolumeClose(IntPtr hFindVolume);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceExW(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailableToCaller,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationW(
            string lpRootPathName,
            [Out] System.Text.StringBuilder? lpVolumeNameBuffer,
            uint nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            [Out] System.Text.StringBuilder? lpFileSystemNameBuffer,
            uint nFileSystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNamesForVolumeNameW(
            string lpszVolumeName,
            [Out] char[] lpszVolumePathNames,
            uint cchBufferLength,
            out uint lpcchReturnLength);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        public const uint EWX_SHUTDOWN = 0x00000001;
        public const uint EWX_FORCE    = 0x00000004;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustTokenPrivileges(
            IntPtr TokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState,
            uint BufferLength,
            IntPtr PreviousState,
            IntPtr ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x00000020;
        public const uint TOKEN_QUERY             = 0x00000008;
        public const uint SE_PRIVILEGE_ENABLED    = 0x00000002;
        public const string SE_SHUTDOWN_NAME      = "SeShutdownPrivilege";
        #endregion
    }
}
