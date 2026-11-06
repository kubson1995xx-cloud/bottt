using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;
namespace Apex.Desktop.Okx;

// Dedicated report store, never the broker financial/history store. Native admission only.
internal sealed class WindowsOkxAdvisoryStore : IDisposable
{
    readonly object sync=new();readonly WindowsPrivateDataLease root;bool disposed;
    internal WindowsOkxAdvisoryStore(string path)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("NATIVE_WINDOWS_ADVISORY_REQUIRED");
        root=WindowsPrivateData.Open(path,AppContext.BaseDirectory);
        try{Scan();}catch{root.Dispose();throw;}
    }
    void Scan()
    {
        root.ValidateTree();int count=0;
        // ValidateTree holds every admitted directory before this enumeration. Never follow reparses.
        foreach(string p in Directory.EnumerateFiles(root.Path,"*",SearchOption.AllDirectories))
        {if(++count>10000)throw new InvalidDataException("ADVISORY_TREE_LIMIT");using var file=Open(p);}
    }
    static WindowsPathSecurity.NativeObject Open(string path)
    {
        var h=WindowsPathSecurity.Open(path);
        try
        {
            WindowsPathSecurity.Validate(h,true);
            if(h.IsDirectory||!GetFileInformationByHandle(h.Handle,out var info)||info.Links!=1)throw new InvalidDataException("ADVISORY_FILE_REFUSED");
            return h;
        }
        catch{h.Dispose();throw;}
    }
    // CURRENT and generation files stay held through final synchronous SendAsync invocation.
    // A writer's atomic replace either precedes this open, or is denied while this lease lives.
    internal Lease? Acquire(string instrument,string digest,DateTimeOffset cutoff,DateTimeOffset now)
    {
        lock(sync)
        {
            if(disposed||!OkxAdvisoryComparison.InstrumentAllowed(instrument))return null;
            var files=new List<IDisposable>();
            try
            {
                Scan();
                byte[] Read(string relative)
                {
                    string path=Path.Combine(root.Path,instrument,relative.Replace('/',Path.DirectorySeparatorChar));
                    var h=Open(path);files.Add(h);var stream=new FileStream(h.Handle,FileAccess.Read);files.Add(stream);
                    int bound=relative.EndsWith("-native.json",StringComparison.Ordinal)?1048576:16384;
                    if(stream.Length is <=0||stream.Length>bound)throw new InvalidDataException("ADVISORY_SIZE");
                    var buffer=new byte[checked((int)stream.Length)];stream.ReadExactly(buffer);if(stream.ReadByte()!=-1)throw new InvalidDataException("ADVISORY_SIZE");return buffer;
                }
                var snapshot=OkxAdvisorySnapshot.Read(Read,instrument,digest,cutoff,now);
                return new(snapshot,files);
            }
            catch(Exception e) when(e is InvalidDataException or IOException or UnauthorizedAccessException or WindowsDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {foreach(var f in files)f.Dispose();return null;}
        }
    }
    internal sealed class Lease(OkxAdvisorySnapshot snapshot,List<IDisposable> files):IDisposable
    {
        internal OkxAdvisorySnapshot Snapshot {get;}=snapshot;
        public void Dispose(){for(int i=files.Count-1;i>=0;i--)files[i].Dispose();files.Clear();}
    }
    public void Dispose(){lock(sync){if(disposed)return;disposed=true;root.Dispose();}}
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
}
