using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KeelMatrix.NuGetReady;

internal readonly record struct ArtifactPathIdentity(string Value, bool IsReparsePoint);

internal static class ArtifactPathIdentityProvider
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint WindowsReparsePoint = 0x0400;
    private const uint UnixSymlinkMode = 0xA000;
    private const uint UnixFileTypeMask = 0xF000;
    private const int UnixReadOnly = 0;
    private const int LinuxNoFollow = 0x20000;
    private const int MacOsNoFollow = 0x100;

    public static ArtifactPathIdentity Capture(string path)
    {
        return OperatingSystem.IsWindows()
            ? CaptureWindows(path)
            : OperatingSystem.IsMacOS()
                ? CaptureMacOs(path)
                : CaptureLinux(path);
    }

    public static FileStream OpenReadNoFollow(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFile(
                    ToWindowsKernelPath(path),
                    GenericRead,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new IOException("Artifact archive could not be opened without following a link.", new Win32Exception(Marshal.GetLastWin32Error()));
                }

                return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }

            var flags = UnixReadOnly | (OperatingSystem.IsMacOS() ? MacOsNoFollow : LinuxNoFollow);
            var descriptor = UnixOpen(path, flags, 0);
            if (descriptor < 0)
            {
                throw new IOException("Artifact archive could not be opened without following a link.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Artifact archive could not be opened without following a link.", exception);
        }
    }

    private static ArtifactPathIdentity CaptureWindows(string path)
    {
        using var handle = CreateFile(
            ToWindowsKernelPath(path),
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("Artifact path identity could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Artifact path identity could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        var value = $"{information.VolumeSerialNumber:x8}:{fileIndex:x16}";
        return new ArtifactPathIdentity(value, (information.FileAttributes & WindowsReparsePoint) != 0);
    }

    private static string ToWindowsKernelPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + path[2..]
            : "\\\\?\\" + path;
    }

    private static ArtifactPathIdentity CaptureLinux(string path)
    {
        if (LinuxLStat(path, out var information) != 0)
        {
            throw new IOException("Artifact path identity could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new ArtifactPathIdentity(
            $"{information.Device:x}:{information.Inode:x}",
            (information.Mode & UnixFileTypeMask) == UnixSymlinkMode);
    }

    private static ArtifactPathIdentity CaptureMacOs(string path)
    {
        if (MacLStat(path, out var information) != 0)
        {
            throw new IOException("Artifact path identity could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new ArtifactPathIdentity(
            $"{information.Device:x}:{information.Inode:x}",
            (information.Mode & UnixFileTypeMask) == UnixSymlinkMode);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    // libc consumes UTF-8 on the supported Unix platforms; the analyzer does
    // not recognize the platform-specific marshaling contract of these calls.
#pragma warning disable CA2101
    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int LinuxLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out LinuxStat information);

    [DllImport("libc", EntryPoint = "lstat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int MacLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat information);

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
#pragma warning restore CA2101

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding;
        public ulong SpecialDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStat
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public int SpecialDevice;
        public TimeSpec AccessTime;
        public TimeSpec ModifyTime;
        public TimeSpec ChangeTime;
        public TimeSpec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        public int Reserved;
        public long Reserved0;
        public long Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
