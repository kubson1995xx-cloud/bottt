using System.Runtime.InteropServices;
using System.Text.Json;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;

// Advisory-only descendant admission. The shared native inspector is unchanged.
// Private directories are pinned before traversing them; mutable leaves are not
// retained across atomic replacement/SQLite journal deletion.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AdvisoryStoreGuard : IDisposable
{
    readonly string root;
    readonly Dictionary<string, WindowsPathSecurity.NativeObject> directories = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, WindowsPathSecurity.NativeObject> files = new();
    int sequence;
    internal AdvisoryStoreGuard(string root) { this.root = root; }
    string Bound(string path)
    {
        string full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
        return full;
    }
    static void Check(WindowsPathSecurity.NativeObject h, bool directory)
    {
        WindowsPathSecurity.Validate(h, true);
        if (h.IsDirectory != directory || (!directory && (!GetFileInformationByHandle(h.Handle, out var info) || info.Links != 1)))
            throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
    }
    internal void Directory(string path)
    {
        path = Bound(path);
        if (directories.TryGetValue(path, out var held)) { Check(held, true); return; }
        if (path != root) Directory(Path.GetDirectoryName(path)!);
        // GetAttributes sees a dangling reparse too; never treat it as absent.
        if (!Exists(path)) WindowsPathSecurity.CreatePrivateDirectory(path);
        var h = WindowsPathSecurity.Open(path);
        try { Check(h, true); directories.Add(path, h); }
        catch { h.Dispose(); throw; }
    }
    static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal void Scan(string path)
    {
        path = Bound(path); Directory(path);
        foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path))
        {
            // Open before classifying: never recurse through a junction/reparse.
            using var h = WindowsPathSecurity.Open(child);
            bool fileName = Path.GetFileName(child) is "CURRENT.json" or "refresh.lock" or "budget.sqlite" or "budget.sqlite-journal" or "budget.sqlite-wal" or "budget.sqlite-shm" || child.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            bool directoryName = Path.GetFileName(child) is "BTC-EUR" or "ETH-EUR" or "SOL-EUR" or "attempts" or "generations";
            Check(h, fileName ? false : directoryName ? true : h.IsDirectory);
            if (h.IsDirectory) Scan(child);
        }
    }
    int Hold(string path, bool create)
    {
        path = Bound(path); Directory(Path.GetDirectoryName(path)!);
        if (create && !Exists(path))
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
        if (!Exists(path)) throw new FileNotFoundException();
        var h = WindowsPathSecurity.Open(path);
        try { Check(h, false); int id = ++sequence; files.Add(id, h); return id; }
        catch { h.Dispose(); throw; }
    }
    internal void Serve()
    {
        Console.WriteLine("ADVISORY_STORE_ADMITTED"); Console.Out.Flush();
        for (string? line; (line = Console.ReadLine()) != null && line != "close";)
        {
            try
            {
                var command = JsonSerializer.Deserialize<string[]>(line)!;
                int id = 0;
                switch (command[0])
                {
                    case "scan": Scan(command[1]); break;
                    case "dir": Directory(command[1]); break;
                    case "file": id = Hold(command[1], true); break;
                    case "hold": id = Hold(command[1], false); break;
                    case "inspect":
                        if (Exists(Bound(command[1]))) { int key = Hold(command[1], false); files[key].Dispose(); files.Remove(key); }
                        break;
                    case "release": int key2 = int.Parse(command[1]); files[key2].Dispose(); files.Remove(key2); break;
                    default: throw new WindowsDataException(WindowsPathSecurity.PathInvalid);
                }
                Console.WriteLine("OK " + id);
            }
            catch (FileNotFoundException) { Console.WriteLine("MISSING"); }
            catch (Exception) { Console.WriteLine("REFUSED"); }
            Console.Out.Flush();
        }
    }
    public void Dispose() { foreach (var h in files.Values) h.Dispose(); foreach (var h in directories.Values.Reverse()) h.Dispose(); }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsPathSecurity.FileInformation info);
}
