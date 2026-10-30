using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Apex.Desktop.Services;

// One Windows inspector for startup and Naval credentials. Inspect the opened
// object, not a second path lookup; OPEN_REPARSE_POINT never follows a leaf link.
[SupportedOSPlatform("windows")]
internal static class WindowsPathSecurity
{
    private const uint ReadControlAndAttributes = 0x00020080;
    internal const string PathInvalid = "HOST_DATA_PATH_INVALID";
    internal const string PermissionsInvalid = "HOST_DATA_PERMISSIONS_INVALID";
    internal const string Unavailable = "HOST_DATA_UNAVAILABLE";
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    internal static void Validate(FileSystemInfo target, bool privateTarget)
    {
        using var opened = Open(target.FullName);
        Validate(opened, privateTarget);
    }

    internal static NativeObject Open(string path)
    {
        var handle = CreateFile(path, ReadControlAndAttributes, 3 /* read/write, never delete sharing */, IntPtr.Zero, 3, 0x02200000 /* backup semantics + open reparse point */, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new WindowsDataException(Unavailable); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new WindowsDataException(Unavailable);
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new WindowsDataException(PathInvalid);
            var buffer = new StringBuilder(32768);
            uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0 || count >= buffer.Capacity) throw new WindowsDataException(Unavailable);
            string canonical = buffer.ToString();
            if (!canonical.StartsWith("\\\\?\\", StringComparison.Ordinal) || canonical.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) throw new WindowsDataException(PathInvalid);
            canonical = canonical[4..];
            return new NativeObject(handle, canonical, info);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void Validate(NativeObject opened, bool privateTarget)
    {
        uint error = GetSecurityInfo(opened.Handle, 1 /* SE_FILE_OBJECT */, 5 /* OWNER | DACL */, out _, out _, out _, out _, out var sd);
        if (error != 0 || sd == IntPtr.Zero) throw new WindowsDataException(PermissionsInvalid);
        try
        {
            uint length = GetSecurityDescriptorLength(sd);
            if (length == 0 || length > 65536) throw new WindowsDataException(PermissionsInvalid);
            var bytes = new byte[length]; Marshal.Copy(sd, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl == null || descriptor.Owner == null) throw new WindowsDataException(PermissionsInvalid);
            var me = WindowsIdentity.GetCurrent().User ?? throw new WindowsDataException(PermissionsInvalid);
            bool Trusted(SecurityIdentifier sid) => sid == me || sid == SystemSid || sid == Administrators;
            if (!Trusted(descriptor.Owner) && (privateTarget || descriptor.Owner != TrustedInstaller)) throw new WindowsDataException(PermissionsInvalid);
            foreach (GenericAce entry in descriptor.DiscretionaryAcl)
            {
                // Object/callback/conditional/resource ACEs cannot establish this boundary.
                if (entry is not CommonAce ace || ace.IsCallback || ace.AceType is not (AceType.AccessAllowed or AceType.AccessDenied)) throw new WindowsDataException(PermissionsInvalid);
                if (ace.AceType == AceType.AccessDenied) continue;
                if (Trusted(ace.SecurityIdentifier)) continue;
                if (!privateTarget && (ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                // Creating siblings is harmless; replacing/deleting this child, editing
                // its ancestry or changing security is not. Generic rights fail closed.
                const int unsafeAncestor = 0x000D0150 | unchecked((int)0x50000000); // DELETE, DELETE_CHILD, WRITE_EA/ATTR, WRITE_DAC/OWNER, GENERIC_WRITE/ALL
                if (privateTarget || (ace.AccessMask & unsafeAncestor) != 0) throw new WindowsDataException(PermissionsInvalid);
            }
        }
        finally { LocalFree(sd); }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        var me = WindowsIdentity.GetCurrent().User ?? throw new WindowsDataException(PermissionsInvalid);
        // Descriptor is attached by CreateDirectoryW itself, never repaired afterward.
        string sddl = "O:" + me.Value + "D:P(A;OICI;FA;;;" + me.Value + ")(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var sd, out _)) throw new WindowsDataException(Unavailable);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd, InheritHandle = false };
            if (!CreateDirectory(path, ref attributes)) throw new WindowsDataException(Unavailable);
        }
        finally { LocalFree(sd); }
    }

    internal static void ValidateVolume(string root)
    {
        if (new DriveInfo(root).DriveType != DriveType.Fixed || !GetVolumeInformation(root, null, 0, out _, out _, out uint flags, null, 0) || (flags & 8) == 0) throw new WindowsDataException(PathInvalid);
    }

    internal sealed class NativeObject : IDisposable
    {
        internal SafeFileHandle Handle { get; }
        internal string Path { get; }
        internal bool IsDirectory => (Identity.Attributes & (uint)FileAttributes.Directory) != 0;
        private readonly FileInformation Identity;
        internal NativeObject(SafeFileHandle handle, string path, FileInformation identity) { Handle = handle; Path = path; Identity = identity; }
        internal bool SameIdentity(NativeObject other) => Identity.VolumeSerial == other.Identity.VolumeSerial && Identity.IndexHigh == other.Identity.IndexHigh && Identity.IndexLow == other.Identity.IndexLow;
        public void Dispose() => Handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")] private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")] private static extern bool GetVolumeInformation(string root, StringBuilder? name, uint nameSize, out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder? fileSystem, uint fileSystemSize);
}
