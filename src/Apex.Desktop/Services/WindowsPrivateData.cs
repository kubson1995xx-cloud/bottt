using System;
using System.IO;

namespace Apex.Desktop.Services;

internal sealed class WindowsDataException : Exception
{
    internal string Code { get; }
    internal WindowsDataException(string code) : base(code) { Code = code; }
}

// RED-only typed adapter: exactly the old Program's create-directory behavior.
// It supplies no security admission and will be replaced only after native RED.
internal static class WindowsPrivateData
{
    internal static WindowsPrivateDataLease Open(string requestedPath, string releaseRoot)
    {
        bool isNew = !Directory.Exists(requestedPath);
        Directory.CreateDirectory(requestedPath);
        return new WindowsPrivateDataLease(requestedPath, isNew);
    }
}
internal sealed class WindowsPrivateDataLease : IDisposable
{
    internal string Path { get; }
    internal bool IsNew { get; }
    internal WindowsPrivateDataLease(string path, bool isNew) { Path = path; IsNew = isNew; }
    internal void ValidateTree() { }
    public void Dispose() { }
}
