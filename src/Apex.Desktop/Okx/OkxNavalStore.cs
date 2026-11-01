using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Apex.Core.Naval;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;
namespace Apex.Desktop.Okx;

// One exact FileStore key. No backup adoption, root repair or portable production fallback.
internal sealed class OkxNavalStore : INavalBrokerStore,IDisposable
{
    internal const string Target="naval_authority_broker_v2.json";
    private readonly FileStore store;
    private readonly WindowsPrivateDataLease? nativeRoot;
    private readonly List<IDisposable> held=new();
    private bool disposed;
    public string DirectoryPath { get; }
    public string StorageIdentity { get; }
    private OkxNavalStore(string root,OkxScopeBinding binding,bool portable,Action<AtomicWriteBoundary>? observer)
    {
        DirectoryPath=OkxConfiguration.Paths(root,binding).Authority;
        try
        {
            if(!portable)
            {
                if(!OperatingSystem.IsWindows())throw new InvalidOperationException("BROKER_NATIVE_STORE_UNAVAILABLE");
                if(!Directory.Exists(root))throw new InvalidDataException("BROKER_ROOT_NOT_ADMITTED");
                nativeRoot=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
                if(!nativeRoot.Path.Equals(Path.TrimEndingDirectorySeparator(root),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("BROKER_ROOT_ALIAS");
                string scope=OkxConfiguration.Paths(root,binding).Root;
                if(!Directory.Exists(scope))throw new InvalidDataException("BROKER_CREDENTIAL_SCOPE_MISSING");
                if(!Directory.Exists(DirectoryPath))WindowsPathSecurity.CreatePrivateDirectory(DirectoryPath);
                var authority=WindowsPathSecurity.Open(DirectoryPath);held.Add(authority);WindowsPathSecurity.Validate(authority,true);
                if(!authority.IsDirectory||!authority.Path.Equals(DirectoryPath,StringComparison.OrdinalIgnoreCase)||!GetFileInformationByHandle(authority.Handle,out var info))throw new InvalidDataException("BROKER_ROOT_INVALID");
                StorageIdentity=Hash(authority.Path.ToUpperInvariant()+"|"+info.VolumeSerial+"|"+info.IndexHigh+"|"+info.IndexLow);
            }
            else
            {
                // Only the existing friend-only synthetic composition can select this branch.
                Directory.CreateDirectory(DirectoryPath);StorageIdentity=Hash(Path.GetFullPath(DirectoryPath));
            }
            ValidateLayout();
            store=observer==null?new FileStore(DirectoryPath):new FileStore(DirectoryPath,observer);
        }
        catch{Dispose();throw;}
    }
    internal static OkxNavalStore OpenNative(string root,OkxScopeBinding binding)=>new(root,binding,false,null);
    internal static OkxNavalStore ForTests(string root,OkxScopeBinding binding,Action<AtomicWriteBoundary>? observer=null)=>new(root,binding,true,observer);
    private static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void ValidateLayout()
    {
        if(disposed)throw new ObjectDisposedException(nameof(OkxNavalStore));
        if(OperatingSystem.IsWindows()&&nativeRoot!=null)nativeRoot.ValidateDirectories();
        foreach(string entry in Directory.EnumerateFileSystemEntries(DirectoryPath))
        {
            string name=Path.GetFileName(entry);
            bool orphan=name.StartsWith(Target+".",StringComparison.Ordinal)&&name.EndsWith(".tmp",StringComparison.Ordinal)&&Guid.TryParseExact(name.Substring(Target.Length+1,name.Length-Target.Length-5),"N",out _);
            if(Directory.Exists(entry)||(File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0||name is not (Target or ".naval-broker-initialized-v2" or ".naval-owner.lock")&&!orphan)throw new InvalidDataException("BROKER_LAYOUT_PRESERVED");
            if(OperatingSystem.IsWindows()&&nativeRoot!=null&&name!=".naval-owner.lock")
            {
                using var opened=WindowsPathSecurity.Open(entry);WindowsPathSecurity.Validate(opened,true);
                if(!GetFileInformationByHandle(opened.Handle,out var info)||info.Links!=1)throw new InvalidDataException("BROKER_FILE_ALIAS");
            }
        }
    }
    public void Admit(bool create)
    {
        ValidateLayout();bool target=File.Exists(Path.Combine(DirectoryPath,Target)),marker=File.Exists(Path.Combine(DirectoryPath,NavalBrokerValidation.Marker));
        if(create)
        {if(target||marker||Directory.EnumerateFileSystemEntries(DirectoryPath).Any(p=>Path.GetFileName(p)!=".naval-owner.lock"))throw new InvalidDataException("BROKER_CREATE_REQUIRES_EMPTY");}
        else if(!target||!marker)throw new InvalidDataException("BROKER_REOPEN_REQUIRES_EXACT_TARGET");
    }
    private void Key(string key){if(disposed||key!=NavalBrokerValidation.StateKey)throw new InvalidOperationException("BROKER_STORE_KEY_DENIED");}
    public string? Get(string key){Key(key);return store.Get(key);}
    public void Set(string key,string value){Key(key);ValidateLayout();store.Set(key,value);}
    public void Flush(){if(disposed)throw new ObjectDisposedException(nameof(OkxNavalStore));store.Flush();}
    public void Dispose(){if(disposed)return;disposed=true;foreach(var item in held.AsEnumerable().Reverse())item.Dispose();if(OperatingSystem.IsWindows())nativeRoot?.Dispose();}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
}
