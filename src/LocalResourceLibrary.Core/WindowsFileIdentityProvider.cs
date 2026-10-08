using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalResourceLibrary.Core;

/// <summary>
/// Captures and resolves local NTFS/ReFS file IDs on demand. No enumeration, privileges,
/// persistent handles, or background activity are required.
/// </summary>
public sealed class WindowsFileIdentityProvider : IFileIdentityProvider
{
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint SupportsOpenByFileId = 0x01000000;
    private const uint VolumeNameGuid = 1;
    private const int FileIdInfoClass = 18;

    public FileIdentity? GetIdentity(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                fullPath = fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + fullPath[2..]
                    : @"\\?\" + fullPath;
            using var handle = CreateFileW(fullPath, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            return handle.IsInvalid ? null : ReadIdentity(handle);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    public string? ResolvePath(FileIdentity identity)
    {
        if (!OperatingSystem.IsWindows() || identity is null ||
            !TryNormalizeVolumeId(identity.VolumeId, out var volumeId) ||
            !TryCreateDescriptor(identity.FileId, out var descriptor))
            return null;
        try
        {
            // A root-directory handle is a volume hint; opening the raw volume would
            // unnecessarily require elevated privileges. This does not enable privileges.
            using var volumeHint = CreateFileW(volumeId, 0, ShareAll, IntPtr.Zero,
                OpenExisting, BackupSemantics, IntPtr.Zero);
            if (volumeHint.IsInvalid) return null;
            using var file = OpenFileById(volumeHint, ref descriptor, 0, ShareAll, IntPtr.Zero, 0);
            if (file.IsInvalid || !Matches(ReadIdentity(file), identity)) return null;

            var finalPath = GetFinalPath(file, flags: 0);
            if (finalPath is null) return null;
            var path = ResourcePaths.Normalize(finalPath);
            // Recheck the usable path as well: a concurrent rename, replacement, or
            // hard-link change must not cause a different physical file to be returned.
            return Matches(GetIdentity(path), identity) ? path : null;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    private static FileIdentity? ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var basic) ||
            (basic.FileAttributes & FileAttributeDirectory) != 0)
            return null;

        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, out _, out _,
                out var flags, fileSystem, (uint)fileSystem.Capacity) ||
            (flags & SupportsOpenByFileId) == 0)
            return null;

        // FAT/exFAT IDs need not survive renames. Network shares have no volume GUID
        // and OpenFileById does not support SMB, so do not promise recovery there.
        var name = fileSystem.ToString();
        string fileId;
        if (name.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            var id = ((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow;
            fileId = id.ToString("X16", CultureInfo.InvariantCulture);
        }
        else if (name.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var extended,
                    (uint)Marshal.SizeOf<FileIdInfo>()))
                return null;
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, extended.FileIdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], extended.FileIdHigh);
            fileId = Convert.ToHexString(bytes);
        }
        else return null;

        var finalPath = GetFinalPath(handle, VolumeNameGuid);
        if (finalPath is null || finalPath.Length < 49 ||
            !TryNormalizeVolumeId(finalPath[..49], out var volumeId))
            return null;
        return new FileIdentity(volumeId, fileId);
    }

    private static bool Matches(FileIdentity? actual, FileIdentity expected) => actual is not null &&
        string.Equals(actual.VolumeId, expected.VolumeId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.FileId, expected.FileId, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeVolumeId(string? value, out string volumeId)
    {
        volumeId = "";
        if (value is null || value.Length != 49 ||
            !value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(@"}\", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.AsSpan(11, 36), "D", out var guid))
            return false;
        volumeId = @"\\?\Volume{" + guid.ToString("D") + @"}\";
        return true;
    }

    private static bool TryCreateDescriptor(string? fileId, out FileIdDescriptor descriptor)
    {
        descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>() };
        if (fileId is null) return false;
        if (fileId.Length == 16 && ulong.TryParse(fileId, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var id))
        {
            descriptor.Type = 0; // FileIdType (NTFS).
            descriptor.FileIdLow = id;
            return true;
        }
        if (fileId.Length != 32 || !fileId.All(char.IsAsciiHexDigit)) return false;
        var bytes = Convert.FromHexString(fileId);
        descriptor.Type = 2; // ExtendedFileIdType (ReFS).
        descriptor.FileIdLow = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        descriptor.FileIdHigh = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        return true;
    }

    private static string? GetFinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new StringBuilder(512);
        // Retry only for buffer sizing; never enumerate the volume or directories.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
            if (length == 0 || length > 32768) return null;
            if (length < buffer.Capacity) return buffer.ToString();
            buffer = new StringBuilder(checked((int)length + 1));
        }
        return null;
    }

    private static bool IsUnavailable(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
        DllNotFoundException or EntryPointNotFoundException;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public uint Type;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,
        int informationClass, out FileIdInfo information, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file,
        IntPtr volumeNameBuffer, uint volumeNameSize, out uint volumeSerialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,
        StringBuilder filePath, uint filePathSize, uint flags);
}
