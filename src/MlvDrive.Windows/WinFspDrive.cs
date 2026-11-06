// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Security.AccessControl;
using Fsp;
using MlvDrive.Core.VirtualFs;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;

namespace MlvDrive.Windows;

/// <summary>
/// The virtual drive, served through WinFsp. Strictly read-only: every request that
/// would change anything is refused as write-protected, so nothing can modify footage
/// through it. The tree it serves can be swapped at any time (drives come and go).
/// </summary>
sealed class WinFspDrive : FileSystemBase
{
    const uint ReadOnlyAttr = (uint)System.IO.FileAttributes.ReadOnly;
    const uint DirectoryAttr = (uint)System.IO.FileAttributes.Directory;

    // Owner/group Administrators; SYSTEM and Administrators full control; everyone read and list.
    static readonly byte[] Security = SecurityBytes("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;WD)");

    volatile VDirectory _root;
    readonly string _volumeLabel;

    public WinFspDrive(VDirectory root, string volumeLabel)
    {
        _root = root;
        _volumeLabel = volumeLabel;
    }

    public void SetRoot(VDirectory root) => _root = root;

    static byte[] SecurityBytes(string sddl)
    {
        var sd = new RawSecurityDescriptor(sddl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.PassQueryDirectoryPattern = false;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = 0x4D4C5644; // "MLVD"
        host.FileSystemName = "MLVDrive";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = 1UL << 40;
        volumeInfo.FreeSize = 0;
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    VNode? Find(string fileName) => VirtualPath.Resolve(_root, fileName);

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        var node = Safe(() => Find(fileName));
        if (node is null) return STATUS_OBJECT_NAME_NOT_FOUND;
        fileAttributes = Attributes(node);
        if (securityDescriptor is not null) securityDescriptor = Security;
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = default!; fileDesc = default!; fileInfo = default; normalizedName = default!;
        var node = Safe(() => Find(fileName));
        if (node is null) return STATUS_OBJECT_NAME_NOT_FOUND;
        fileNode = node;
        fileDesc = node;
        fileInfo = Info(node);
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = Info((VNode)fileNode);
        return STATUS_SUCCESS;
    }

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (fileNode is not VFile file) return STATUS_INVALID_DEVICE_REQUEST;
        if (offset >= (ulong)file.Size) return STATUS_END_OF_FILE;
        try
        {
            int n = file.Read((long)offset, new Span<byte>((void*)buffer, (int)length));
            bytesTransferred = (uint)n;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            return STATUS_IO_DEVICE_ERROR;
        }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        fileName = default!; fileInfo = default;
        if (fileNode is not VDirectory dir) return false;

        if (context is not IEnumerator<(string Name, VNode Node)> e)
        {
            var entries = new List<(string, VNode)>();
            if (!ReferenceEquals(dir, _root)) { entries.Add((".", dir)); entries.Add(("..", dir)); }
            IReadOnlyList<VNode> children = Safe(() => dir.Children()) ?? Array.Empty<VNode>();
            entries.AddRange(children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => (c.Name, c)));
            if (marker is not null)
            {
                // continue after the last name returned before
                int i = entries.FindIndex(x => string.Compare(x.Item1, marker, StringComparison.OrdinalIgnoreCase) > 0 && x.Item1 is not "." and not "..");
                entries = i < 0 ? new List<(string, VNode)>() : entries.GetRange(i, entries.Count - i);
            }
            e = entries.GetEnumerator();
            context = e;
        }
        if (!e.MoveNext()) return false;
        fileName = e.Current.Name;
        fileInfo = Info(e.Current.Node);
        return true;
    }

    public override void Close(object fileNode, object fileDesc) { }

    // ---- everything that would change the drive: refused ----
    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = default!; fileDesc = default!; fileInfo = default; normalizedName = default!;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile,
        bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0; fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) => STATUS_MEDIA_WRITE_PROTECTED;
    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo) { volumeInfo = default; return STATUS_MEDIA_WRITE_PROTECTED; }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = Security;
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception ex)
    {
        Log.Write("filesystem error: " + ex);
        return STATUS_UNEXPECTED_IO_ERROR;
    }

    // ---- helpers ----
    static uint Attributes(VNode node) => node is VDirectory ? DirectoryAttr | ReadOnlyAttr : ReadOnlyAttr;

    static FileInfo Info(VNode node)
    {
        var info = default(FileInfo);
        info.FileAttributes = Attributes(node);
        if (node is VFile f)
        {
            info.FileSize = (ulong)f.Size;
            info.AllocationSize = ((ulong)f.Size + 4095) / 4096 * 4096;
        }
        ulong t = (ulong)SafeFileTime(node.Modified);
        info.CreationTime = info.LastAccessTime = info.LastWriteTime = info.ChangeTime = t;
        return info;
    }

    static long SafeFileTime(DateTime t)
    {
        try { return t.ToFileTimeUtc(); }
        catch (ArgumentOutOfRangeException) { return DateTime.UtcNow.ToFileTimeUtc(); }
    }

    static T? Safe<T>(Func<T?> f) where T : class
    {
        try { return f(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("lookup failed: " + ex.Message);
            return null;
        }
    }
}
