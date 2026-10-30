using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace Apex.Desktop.Services;

internal sealed class WindowsDataException : Exception
{
    internal string Code { get; }
    internal WindowsDataException(string code) : base(code) { Code = code; }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPrivateData
{
    internal static WindowsPrivateDataLease Open(string requestedPath, string releaseRoot)
    {
        var lease = new WindowsPrivateDataLease();
        try
        {
            string full = ValidateSyntax(requestedPath);
            string volume = System.IO.Path.GetPathRoot(full)!;
            WindowsPathSecurity.ValidateVolume(volume);
            using var release = WindowsPathSecurity.Open(ValidateSyntax(releaseRoot));
            if (!release.IsDirectory) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
            var chain = Components(full);
            string canonical = volume;
            bool missing = false;
            for (int i = 0; i < chain.Count; i++)
            {
                string path = i == 0 ? chain[i] : System.IO.Path.Combine(canonical, System.IO.Path.GetFileName(chain[i]));
                bool exists = Exists(path);
                if (!exists) missing = true;
                if (exists)
                {
                    var opened = WindowsPathSecurity.Open(path);
                    try
                    {
                        if (!opened.IsDirectory) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
                        canonical = opened.Path;
                        CheckLocation(canonical, release.Path);
                        WindowsPathSecurity.Validate(opened, i == chain.Count - 1);
                        lease.Hold(opened, i == chain.Count - 1); opened = null!;
                    }
                    finally { opened?.Dispose(); }
                }
                else
                {
                    canonical = path;
                    CheckLocation(canonical, release.Path);
                    WindowsPathSecurity.CreatePrivateDirectory(canonical);
                    var opened = WindowsPathSecurity.Open(canonical);
                    try { WindowsPathSecurity.Validate(opened, true); lease.Hold(opened, true); opened = null!; }
                    finally { opened?.Dispose(); }
                }
            }
            lease.Initialize(canonical, missing);
            lease.ValidateTree();
            return lease;
        }
        catch (WindowsDataException) { lease.Dispose(); throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { lease.Dispose(); throw new WindowsDataException(WindowsPathSecurity.Unavailable); }
    }

    internal static string ValidateSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 24000 || path.StartsWith("\\\\", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path[2..].Any(c => c == ':' || c == '/' || c == '%' || c == '*' || c == '?' || char.IsControl(c))) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
        string trimmed = System.IO.Path.TrimEndingDirectorySeparator(path);
        foreach (string component in trimmed[3..].Split('\\'))
            if (component is "." or ".." || component.EndsWith(' ') || component.EndsWith('.') || component.Length == 0 && trimmed.Length > 3) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
        string full = System.IO.Path.GetFullPath(trimmed);
        if (!full.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
        return full;
    }

    internal static List<string> Components(string full)
    {
        var chain = new List<string>();
        for (var d = new DirectoryInfo(full); d != null; d = d.Parent) chain.Add(d.FullName);
        chain.Reverse(); return chain;
    }
    internal static bool Contains(string parent, string child)
    {
        parent = System.IO.Path.TrimEndingDirectorySeparator(parent);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.EndsWith('\\') ? parent : parent + "\\", StringComparison.OrdinalIgnoreCase);
    }
    internal static void CheckLocation(string path, string releaseRoot)
    {
        if (Contains(releaseRoot, path)) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
        CheckMarkers(path);
    }
    internal static void CheckMarkers(string path)
    {
        if (File.Exists(System.IO.Path.Combine(path, ".naval-owner.lock")) || File.Exists(System.IO.Path.Combine(path, ".naval-initialized")) || File.Exists(System.IO.Path.Combine(path, ".git")) || Directory.Exists(System.IO.Path.Combine(path, ".git")) || File.Exists(System.IO.Path.Combine(path, "APEX.Trading.Demo.sln"))) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
    }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

// Hold ancestors and every inspected private directory without FILE_SHARE_DELETE.
// Files are opened without following links for ACL inspection and then released:
// keeping files open would break legitimate atomic replacement and apex.lock.
[SupportedOSPlatform("windows")]
internal sealed class WindowsPrivateDataLease : IDisposable
{
    private readonly Dictionary<string, (WindowsPathSecurity.NativeObject Object, bool Private)> held = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    internal string Path { get; private set; } = "";
    internal bool IsNew { get; private set; }
    internal void Initialize(string path, bool isNew) { Path = path; IsNew = isNew; }
    internal void Hold(WindowsPathSecurity.NativeObject opened, bool privateTarget)
    {
        if (held.Count >= 4096) throw new WindowsDataException(WindowsPathSecurity.Unavailable);
        held.Add(opened.Path, (opened, privateTarget));
    }
    internal void ValidateTree()
    {
        if (disposed) throw new WindowsDataException(WindowsPathSecurity.Unavailable);
        try
        {
            foreach (var item in held.Values)
            {
                using var current = WindowsPathSecurity.Open(item.Object.Path);
                if (!current.SameIdentity(item.Object)) throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
                WindowsPathSecurity.Validate(current, item.Private);
            }
            int count = 0;
            void Visit(string directory, int depth)
            {
                if (depth > 64) throw new WindowsDataException(WindowsPathSecurity.Unavailable);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++count > 10000) throw new WindowsDataException(WindowsPathSecurity.Unavailable);
                    var opened = WindowsPathSecurity.Open(path);
                    try
                    {
                        WindowsPathSecurity.Validate(opened, true);
                        if (opened.IsDirectory)
                        {
                            string canonical = opened.Path;
                            if (held.TryGetValue(canonical, out var previous))
                            { if (!opened.SameIdentity(previous.Object)) throw new WindowsDataException(WindowsPathSecurity.PathInvalid); }
                            else { Hold(opened, true); opened = null!; }
                            Visit(canonical, depth + 1);
                        }
                    }
                    finally { opened?.Dispose(); }
                }
            }
            Visit(Path, 0);
        }
        catch (WindowsDataException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new WindowsDataException(WindowsPathSecurity.Unavailable); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var value in held.Values.Reverse()) value.Object.Dispose();
        held.Clear();
    }
}
