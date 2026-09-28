using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KeelMatrix.NuGetReady;

internal sealed class ArtifactTreeHandle : IDisposable
{
    private bool disposed;

    private ArtifactTreeHandle(ArtifactDirectoryHandle root)
    {
        Root = root;
    }

    public ArtifactDirectoryHandle Root { get; }

    public static ArtifactTreeHandle Open(string path)
    {
        return new ArtifactTreeHandle(ArtifactDirectoryHandle.OpenPath(Path.GetFullPath(path)));
    }

    public bool VerifyBinding()
    {
        return !disposed && Root.VerifyBinding();
    }

    public ArtifactFileHandle OpenFile(
        string relativePath,
        IReadOnlyDictionary<string, ArtifactTreeEntry> expectedEntries)
    {
        if (!VerifyBinding())
        {
            throw new IOException("The artifact tree identity changed before a source file could be opened.");
        }

        var current = Root;
        var openedDirectories = new List<ArtifactDirectoryHandle>();
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new IOException("The artifact source path was empty.");
        }

        for (var index = 0; index < segments.Length; index++)
        {
            var prefix = string.Join('/', segments.Take(index + 1));
            if (!expectedEntries.TryGetValue(prefix, out var expected))
            {
                throw new IOException("The artifact tree entry was not present in the accepted scan.");
            }

            var child = current.OpenChild(segments[index]);
            if (child.IsReparsePoint || !string.Equals(child.Identity, expected.Identity, StringComparison.Ordinal))
            {
                child.Dispose();
                throw new IOException($"Artifact '{relativePath}' changed identity before it could be opened.");
            }

            if (index == segments.Length - 1)
            {
                if (child.IsDirectory)
                {
                    child.Dispose();
                    throw new IOException($"Artifact '{relativePath}' is no longer a file.");
                }

                var file = new ArtifactFileHandle(child);
                if (!VerifyBinding())
                {
                    file.Dispose();
                    throw new IOException("The artifact tree identity changed before a source file could be read.");
                }

                foreach (var openedDirectory in openedDirectories)
                {
                    openedDirectory.Dispose();
                }

                return file;
            }

            if (!child.IsDirectory)
            {
                child.Dispose();
                throw new IOException($"Artifact directory '{prefix}' is no longer a directory.");
            }

            openedDirectories.Add(child);
            current = child;
        }

        foreach (var openedDirectory in openedDirectories)
        {
            openedDirectory.Dispose();
        }

        throw new IOException("The artifact source path could not be opened.");
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            var current = Root;
            while (current is not null)
            {
                var parent = current.Parent;
                current.Dispose();
                current = parent;
            }
        }
    }
}

internal sealed class ArtifactDirectoryHandle : IDisposable
{
    private bool disposed;

    private ArtifactDirectoryHandle(
        SafeFileHandle handle,
        ArtifactDirectoryHandle? parent,
        string name,
        ArtifactPathIdentity identity,
        FileAttributes attributes)
    {
        Handle = handle;
        Parent = parent;
        Name = name;
        Identity = identity.Value;
        IsReparsePoint = identity.IsReparsePoint;
        Attributes = attributes;
    }

    public SafeFileHandle Handle { get; }
    public ArtifactDirectoryHandle? Parent { get; }
    public string Name { get; }
    public string Identity { get; }
    public bool IsReparsePoint { get; }
    public FileAttributes Attributes { get; }

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public static ArtifactDirectoryHandle OpenPath(string path)
    {
        var traversalPath = ArtifactNative.NormalizeTraversalPath(path);
        var rootHandle = ArtifactNative.OpenFilesystemRoot(traversalPath);
        var rootInfo = ArtifactNative.GetInfo(rootHandle);
        var current = new ArtifactDirectoryHandle(rootHandle, null, string.Empty, rootInfo.Identity, rootInfo.Attributes);
        var rootName = Path.GetPathRoot(traversalPath) ?? string.Empty;
        var remainder = traversalPath[rootName.Length..];
        foreach (var segment in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            var child = current.OpenChild(segment);
            if (child.IsReparsePoint)
            {
                child.Dispose();
                current.Dispose();
                throw new ArtifactTreeLimitExceededException("Artifact tree has a reparse-point ancestor; links and junctions are not followed.");
            }

            if (!child.IsDirectory)
            {
                child.Dispose();
                current.Dispose();
                throw new DirectoryNotFoundException("Artifact directory was not found.");
            }

            current = child;
        }

        current.EnsureDirectory();
        return current;
    }

    public IReadOnlyList<string> EnumerateNames()
    {
        EnsureUsable();
        return ArtifactNative.EnumerateNames(Handle);
    }

    public ArtifactDirectoryHandle OpenChild(string name)
    {
        return OpenChild(name, exclusiveForLaunch: false);
    }

    internal ArtifactDirectoryHandle OpenChild(string name, bool exclusiveForLaunch, Action? beforeAttribute = null)
    {
        EnsureUsable();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
        {
            throw new IOException("The artifact tree contained an invalid child name.");
        }

        SafeFileHandle childHandle;
        try
        {
            childHandle = ArtifactNative.OpenRelative(Handle, name, exclusiveForLaunch);
        }
        catch (ArtifactReparsePointException)
        {
            throw new ArtifactTreeLimitExceededException($"Artifact tree contains a reparse point at '{name}'; links and junctions are not followed.");
        }

        try
        {
            beforeAttribute?.Invoke();
            var info = ArtifactNative.GetInfo(childHandle);
            return new ArtifactDirectoryHandle(childHandle, this, name, info.Identity, info.Attributes);
        }
        catch
        {
            childHandle.Dispose();
            throw;
        }
    }

    public bool VerifyBinding()
    {
        if (disposed || Handle.IsInvalid)
        {
            return false;
        }

        if (Parent is null)
        {
            return true;
        }

        if (!Parent.VerifyBinding())
        {
            return false;
        }

        try
        {
            using var current = Parent.OpenChild(Name);
            return !current.IsReparsePoint && string.Equals(current.Identity, Identity, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ArtifactNodeInfo GetInfo()
    {
        EnsureUsable();
        return ArtifactNative.GetInfo(Handle);
    }

    private void EnsureDirectory()
    {
        if (!IsDirectory)
        {
            throw new DirectoryNotFoundException("Artifact directory was not found.");
        }

        if (IsReparsePoint)
        {
            throw new ArtifactTreeLimitExceededException("Artifact tree root is a reparse point; links and junctions are not followed.");
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(disposed || Handle.IsInvalid, nameof(ArtifactDirectoryHandle));
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            Handle.Dispose();
        }
    }
}

internal sealed class ArtifactFileHandle : IDisposable
{
    private bool disposed;

    internal ArtifactFileHandle(ArtifactDirectoryHandle node)
    {
        Node = node;
    }

    public ArtifactDirectoryHandle Node { get; }

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(disposed, nameof(ArtifactFileHandle));

        return new FileStream(
            ArtifactNative.Duplicate(Node.Handle),
            FileAccess.Read,
            64 * 1024,
            isAsync: false);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            Node.Dispose();
        }
    }
}

internal readonly record struct ArtifactNodeInfo(
    ArtifactPathIdentity Identity,
    FileAttributes Attributes,
    long Length,
    long LastWriteUtcTicks,
    long CreationUtcTicks)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

internal sealed class ArtifactReparsePointException : IOException
{
    public ArtifactReparsePointException(string message)
        : base(message)
    {
    }
}

internal static class ArtifactNative
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint WindowsReparsePoint = 0x0400;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectAttributesCaseInsensitive = 0x00000040;
    private const int FileBothDirectoryInformation = 3;
    private const int StatusSuccess = 0;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int UnixReadOnly = 0;
    private const int UnixNoFollowLinux = 0x20000;
    private const int UnixNoFollowMacOs = 0x100;
    private const int UnixSymlinkMode = 0xA000;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectoryMode = 0x4000;
    private const int UnixDirectoryMask = 0xF000;
    private const int UnixLoopLinux = 40;
    private const int UnixLoopMacOs = 62;

    public static string NormalizeTraversalPath(string path)
    {
        return NormalizeUnixPath(path);
    }

    public static SafeFileHandle OpenFilesystemRoot(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                throw new DirectoryNotFoundException("Artifact directory was not found.");
            }

            var handle = CreateFile(
                ToWindowsKernelPath(root),
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException("Artifact directory could not be opened.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return handle;
        }

        var normalized = NormalizeUnixPath(path);
        var descriptor = UnixOpen(
            normalized == "/" ? normalized : "/",
            UnixReadOnly | (OperatingSystem.IsMacOS() ? UnixNoFollowMacOs : UnixNoFollowLinux),
            0);
        if (descriptor < 0)
        {
            throw new IOException("Artifact directory could not be opened.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    public static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool exclusiveForLaunch = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return OpenWindowsRelative(parent, name, exclusiveForLaunch);
        }

        var flags = UnixReadOnly | (OperatingSystem.IsMacOS() ? UnixNoFollowMacOs : UnixNoFollowLinux);
        var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, 0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is UnixLoopLinux or UnixLoopMacOs)
            {
                throw new ArtifactReparsePointException("The artifact entry is a symbolic link.");
            }

            throw new IOException("Artifact entry could not be opened.", new Win32Exception(error));
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    public static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!DuplicateHandle(
                    GetCurrentProcess(),
                    handle,
                    GetCurrentProcess(),
                    out var duplicate,
                    0,
                    false,
                    DuplicateSameAccess))
            {
                throw new IOException("Artifact file handle could not be duplicated.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return duplicate;
        }

        var descriptor = UnixDup(handle.DangerousGetHandle().ToInt32());
        if (descriptor < 0)
        {
            throw new IOException("Artifact file descriptor could not be duplicated.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    public static ArtifactNodeInfo GetInfo(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException("Artifact entry could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            var identity = new ArtifactPathIdentity(
                $"{information.VolumeSerialNumber:x8}:{index:x16}",
                (information.FileAttributes & WindowsReparsePoint) != 0);
            var attributes = (FileAttributes)information.FileAttributes;
            return new ArtifactNodeInfo(
                identity,
                attributes,
                ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
                FileTimeTicks(information.LastWriteTime),
                FileTimeTicks(information.CreationTime));
        }

        if (OperatingSystem.IsMacOS())
        {
            if (MacFStat(handle.DangerousGetHandle().ToInt32(), out var information) != 0)
            {
                throw new IOException("Artifact entry could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var mode = information.Mode;
            var identity = new ArtifactPathIdentity(
                $"{information.Device:x}:{information.Inode:x}",
                (mode & UnixFileTypeMask) == UnixSymlinkMode);
            var attributes = (mode & UnixFileTypeMask) == UnixDirectoryMode ? FileAttributes.Directory : FileAttributes.Normal;
            return new ArtifactNodeInfo(identity, attributes, information.Size, UnixTimeTicks(information.ModifyTime), UnixTimeTicks(information.BirthTime));
        }

        if (LinuxFStat(handle.DangerousGetHandle().ToInt32(), out var linux) == 0)
        {
            var identity = new ArtifactPathIdentity(
                $"{linux.Device:x}:{linux.Inode:x}",
                (linux.Mode & UnixFileTypeMask) == UnixSymlinkMode);
            var attributes = (linux.Mode & UnixFileTypeMask) == UnixDirectoryMode ? FileAttributes.Directory : FileAttributes.Normal;
            return new ArtifactNodeInfo(identity, attributes, linux.Size, UnixTimeTicks((linux.ModifySeconds, linux.ModifyNanoseconds)), UnixTimeTicks((linux.ChangeSeconds, linux.ChangeNanoseconds)));
        }

        throw new IOException("Artifact entry could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public static IReadOnlyList<string> EnumerateNames(SafeFileHandle handle)
    {
        return OperatingSystem.IsWindows()
            ? EnumerateWindows(handle)
            : EnumerateUnix(handle);
    }

    private static List<string> EnumerateWindows(SafeFileHandle handle)
    {
        var names = new List<string>();
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            var restart = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(
                    handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _,
                    buffer,
                    64 * 1024,
                    FileBothDirectoryInformation,
                    returnSingleEntry: false,
                    IntPtr.Zero,
                    restart);
                restart = false;
                if (status == StatusNoMoreFiles)
                {
                    break;
                }

                if (status != StatusSuccess && status != StatusBufferOverflow)
                {
                    throw new IOException("Artifact directory could not be enumerated.", new Win32Exception(status));
                }

                var offset = 0;
                while (true)
                {
                    var nextOffset = Marshal.ReadInt32(buffer, offset);
                    var attributes = (FileAttributes)(uint)Marshal.ReadInt32(buffer, offset + 56);
                    var nameLength = Marshal.ReadInt32(buffer, offset + 60);
                    var name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 94), nameLength / 2) ?? string.Empty;
                    if (name is not "." and not "..")
                    {
                        names.Add(name);
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }

                    offset += nextOffset;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return names;
    }

    private static List<string> EnumerateUnix(SafeFileHandle handle)
    {
        // dup() shares the directory stream offset with the held descriptor.
        // A second scan would therefore start at EOF after the first scan. Open
        // the held directory itself through openat(".") to obtain an independent
        // open-file description while keeping enumeration handle-relative.
        var enumerationDescriptor = UnixOpenAt(
            handle.DangerousGetHandle().ToInt32(),
            ".",
            UnixReadOnly | (OperatingSystem.IsMacOS() ? UnixNoFollowMacOs : UnixNoFollowLinux),
            0);
        if (enumerationDescriptor < 0)
        {
            throw new IOException("Artifact directory could not be enumerated.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var directory = FdOpenDir(enumerationDescriptor);
        if (directory == IntPtr.Zero)
        {
            _ = UnixClose(enumerationDescriptor);
            throw new IOException("Artifact directory could not be enumerated.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var names = new List<string>();
        try
        {
            while (true)
            {
                var entry = ReadDir(directory);
                if (entry == IntPtr.Zero)
                {
                    break;
                }

                var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
                var name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, nameOffset)) ?? string.Empty;
                if (name is not "." and not "..")
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            _ = CloseDir(directory);
        }

        return names;
    }

    private static SafeFileHandle OpenWindowsRelative(SafeFileHandle parent, string name, bool exclusiveForLaunch)
    {
        var namePointer = Marshal.StringToHGlobalUni(name);
        var objectName = new UnicodeString
        {
            Length = checked((ushort)(name.Length * 2)),
            MaximumLength = checked((ushort)(name.Length * 2)),
            Buffer = namePointer
        };
        var objectNamePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
        try
        {
            Marshal.StructureToPtr(objectName, objectNamePointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = objectNamePointer,
                Attributes = ObjectAttributesCaseInsensitive
            };
            Marshal.StructureToPtr(attributes, attributesPointer, fDeleteOld: false);
            var status = NtCreateFile(
                out var handle,
                FileListDirectory | FileReadAttributes | Synchronize,
                attributesPointer,
                out _,
                IntPtr.Zero,
                0,
                exclusiveForLaunch ? FileShareRead : FileShareRead | FileShareWrite | FileShareDelete,
                FileOpenExisting,
                FileSynchronousIoNonalert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                if (handle is not null)
                {
                    handle.Dispose();
                }

                throw new IOException("Artifact entry could not be opened.", new Win32Exception(status));
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(attributesPointer);
            Marshal.FreeHGlobal(objectNamePointer);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private static long FileTimeTicks(System.Runtime.InteropServices.ComTypes.FILETIME value)
    {
        var fileTime = ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        return DateTime.FromFileTimeUtc(fileTime).Ticks;
    }

    private static long UnixTimeTicks((long Seconds, long Nanoseconds) value)
    {
        return DateTime.UnixEpoch.Ticks + checked(value.Seconds * TimeSpan.TicksPerSecond) + value.Nanoseconds / 100;
    }

    private static string NormalizeUnixPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS())
        {
            return full;
        }

        return full == "/var" || full.StartsWith("/var/", StringComparison.Ordinal)
            ? "/private" + full
            : full == "/tmp" || full.StartsWith("/tmp/", StringComparison.Ordinal)
                ? "/private" + full
                : full;
    }

    private const uint FileOpenExisting = 1;
    private const uint DuplicateSameAccess = 2;

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
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        SafeFileHandle sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        IntPtr objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        IntPtr @event,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.Bool)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.Bool)] bool restartScan);

#pragma warning disable CA2101
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixOpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
#pragma warning restore CA2101

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int LinuxFStat(int descriptor, out LinuxStat information);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFStat(int descriptor, out MacStat information);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int UnixDup(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FdOpenDir(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDir(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDir(IntPtr directory);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

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
        public static implicit operator (long Seconds, long Nanoseconds)(TimeSpec value) => (value.Seconds, value.Nanoseconds);
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
}
